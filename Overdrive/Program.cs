// Program.cs — io_uring /pp server: SQPOLL + multishot accept + single-shot recv (no buf-ring)
// Uses a per-connection 8 KiB unmanaged buffer (simple pool) to avoid races.

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static Overdrive.Native;

unsafe class Program
{
    // ---- Prebuilt OK response ----
    static byte* OK_PTR; static nuint OK_LEN;
    static void InitOk()
    {
        const string s = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nOK";
        var a = Encoding.ASCII.GetBytes(s);
        OK_LEN = (nuint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc((nuint)a.Length);
        for (int i = 0; i < a.Length; i++) OK_PTR[i] = a[i];
    }

    static int ParseOne(byte* p, int len)
    {
        for (int i = 3; i < len; i++)
            if (p[i - 3] == '\r' && p[i - 2] == '\n' && p[i - 1] == '\r' && p[i] == '\n')
                return i + 1;
        return 0;
    }

    static io_uring_sqe* SqeGet(IntPtr ring)
    {
        var sqe = shim_get_sqe_h(ring);
        if (sqe == null) { shim_submit_h(ring); sqe = shim_get_sqe_h(ring); }
        return sqe;
    }

    static void SubmitSend(IntPtr ring, int fd, byte* buf, nuint off, nuint len)
    {
        var sqe = SqeGet(ring);
        shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
        // no IOSQE_ASYNC here
        shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
    }

    static void ArmRecvSingle(IntPtr ring, int fd, byte* buf, uint len)
    {
        var sqe = SqeGet(ring);
        shim_prep_recv(sqe, fd, buf, len, 0);
        // no IOSQE_ASYNC here
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
        shim_submit_h(ring);
    }

    static int CreateListen(string ip, ushort port)
    {
        int lfd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;
        setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int));
        setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int));
        sockaddr_in addr = default;
        addr.sin_family = (ushort)AF_INET;
        addr.sin_port = Htons(port);
        var ipb = Encoding.ASCII.GetBytes(ip + "\0");
        fixed (byte* pip = ipb) inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);
        bind(lfd, &addr, (uint)sizeof(sockaddr_in));
        listen(lfd, BACKLOG);
        int fl = fcntl(lfd, F_GETFL, 0); fcntl(lfd, F_SETFL, fl | O_NONBLOCK);
        return lfd;
    }

    sealed class Conn
    {
        public int Fd;
        public nuint OutOff, OutLen;
        public byte* OutBuf;

        public byte* InBuf;   // per-connection buffer
        public uint  InLen;

        public Conn(int fd) { Fd = fd; }
    }

    static volatile bool StopAll = false;

    static class Worker
    {
        public static void Run(string ip, ushort port, int workerIndex)
        {
            Conn?[] Conns = new Conn?[MAX_FD];

            // ---- Simple unmanaged buffer pool (8 KiB blocks) ----
            const int POOL_BLOCKS = 8192; // 8192 * 8 KiB ≈ 64 MiB per worker (tune)
            byte* poolBase = (byte*)NativeMemory.AlignedAlloc((nuint)(POOL_BLOCKS * RECV_BUF_SZ), 64);
            int   poolTop  = 0;
            var   freeIdx  = new ConcurrentStack<int>();

            byte* RentBuf()
            {
                if (freeIdx.TryPop(out int idx))
                    return poolBase + (nuint)idx * (nuint)RECV_BUF_SZ;
                int my = Interlocked.Increment(ref poolTop) - 1;
                if (my >= POOL_BLOCKS) return null;
                return poolBase + (nuint)my * (nuint)RECV_BUF_SZ;
            }

            void ReturnBuf(byte* p)
            {
                if (p == null) return;
                long off = p - poolBase;
                if (off < 0) return;
                int idx = (int)(off / RECV_BUF_SZ);
                freeIdx.Push(idx);
            }

            // ---- Create ring ----
            const uint ENTRIES = 256;
            int err;
            IntPtr ring = shim_ring_create(ENTRIES, 2000, out err);
            if (ring == IntPtr.Zero)
            {
                Console.Error.WriteLine($"shim_ring_create failed: {err}");
                return;
            }
            Console.WriteLine($"SQPOLL ring ok, fd={shim_ring_fd(ring)}");

            int lfd = CreateListen(ip, port);

            // ---- Multishot accept ----
            {
                var sqe = SqeGet(ring);
                shim_prep_multishot_accept(sqe, lfd, SOCK_NONBLOCK);
                // you can keep or remove async here; it's usually neutral
                // shim_sqe_set_async(sqe);
                shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, lfd));
                shim_submit_h(ring);
            }

            var cqes = new io_uring_cqe*[BATCH_CQES];
            int one = 1;

            while (!StopAll)
            {
                int got;
                fixed (io_uring_cqe** pC = cqes)
                    got = shim_peek_batch_cqe_h(ring, pC, (uint)BATCH_CQES);

                if (got <= 0)
                {
                    io_uring_cqe* oneCqe = null;
                    if (shim_wait_cqe_h(ring, &oneCqe) != 0) continue;
                    cqes[0] = oneCqe; got = 1;
                }

                for (int i = 0; i < got; i++)
                {
                    var cqe = cqes[i];
                    ulong ud = shim_cqe_get_data64(cqe);
                    var kind = UdKindOf(ud);
                    int res = cqe->res;

                    if (kind == UdKind.Accept)
                    {
                        if (res >= 0)
                        {
                            int fd = res;
                            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));
                            var c = Conns[fd]; if (c is null) Conns[fd] = c = new Conn(fd);

                            c.OutBuf = null; c.OutOff = 0; c.OutLen = 0;

                            c.InBuf = RentBuf();
                            if (c.InBuf == null) { Conns[fd] = null; close(fd); continue; }
                            c.InLen = (uint)RECV_BUF_SZ;

                            // arm first recv immediately, using per-conn buffer
                            ArmRecvSingle(ring, fd, c.InBuf, c.InLen);
                        }
                    }
                    else if (kind == UdKind.Recv)
                    {
                        int fd = UdFdOf(ud);
                        var c = Conns[fd];

                        if (res <= 0 || c is null)
                        {
                            if (c is not null) { ReturnBuf(c.InBuf); Conns[fd] = null; }
                            close(fd);
                        }
                        else
                        {
                            var basePtr = c.InBuf;
                            int usedTotal = 0;
                            while (true)
                            {
                                int used = ParseOne(basePtr + usedTotal, res - usedTotal);
                                if (used <= 0) break;
                                usedTotal += used;
                                c.OutBuf = OK_PTR; c.OutLen = OK_LEN; c.OutOff = 0;
                                SubmitSend(ring, c.Fd, c.OutBuf, c.OutOff, c.OutLen);
                            }

                            // re-arm recv for more pipelined requests on same connection
                            ArmRecvSingle(ring, fd, c.InBuf, c.InLen);
                        }
                    }
                    else if (kind == UdKind.Send)
                    {
                        int fd = UdFdOf(ud);
                        var c = Conns[fd];
                        if (c is null || res <= 0)
                        {
                            if (c is not null) { ReturnBuf(c.InBuf); Conns[fd] = null; }
                            close(fd);
                        }
                        else
                        {
                            c.OutOff += (nuint)res;
                            if (c.OutOff < c.OutLen)
                                SubmitSend(ring, c.Fd, c.OutBuf, c.OutOff, c.OutLen);
                            // else keep-alive; next recv already armed
                        }
                    }

                    shim_cqe_seen_h(ring, cqe);
                }

                if (shim_sq_ready_h(ring) > 0) shim_submit_h(ring);
            }

            close(lfd);
            shim_ring_destroy(ring);
            // Optional: free poolBase via NativeMemory.AlignedFree(poolBase);
        }
    }

    static void Main()
    {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        int workers = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("WORKERS"), out var w) ? w : Environment.ProcessorCount);
        workers = 16;

        var threads = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            int wi = i;
            threads[i] = new Thread(() =>
            {
                try { Worker.Run(LISTEN_IP, LISTEN_PORT, wi); }
                catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
            })
            { IsBackground = true, Name = $"uring-w{wi}" };
            threads[i].Start();
        }

        while (!StopAll) Thread.Sleep(250);
        foreach (var t in threads) t.Join();
    }
}
