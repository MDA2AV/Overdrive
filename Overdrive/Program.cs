// Program.cs — Multi-threaded io_uring /pp server (multishot accept + multishot recv + buf-ring)
// Listens on 0.0.0.0:8080. Workers = WORKERS env (default: Environment.ProcessorCount).
// Enable SQPOLL: SQPOLL=1 (needs CAP_SYS_ADMIN/root). Requires liburingshim.so next to the binary.

using System.Runtime.InteropServices;
using System.Text;
using static Overdrive.Native;

unsafe class Program
{
    // ---- Affinity ----
    static class Affinity
    {
        const long SYS_gettid = 186;
        [DllImport("libc")] static extern long syscall(long n);
        [DllImport("libc")] static extern int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);
        public static void PinCurrentThreadToCpu(int cpu)
        {
            int tid = (int)syscall(SYS_gettid);
            int bytes = (Environment.ProcessorCount + 7) / 8;
            var mask = new byte[Math.Max(bytes, 8)];
            mask[cpu/8] |= (byte)(1 << (cpu%8));
            _ = sched_setaffinity(tid, (nuint)mask.Length, mask);
        }
    }

    // ---- Unmanaged OK buffer (shared) ----
    static byte* OK_PTR; static nuint OK_LEN;
    static void InitOk()
    {
        var s = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nOK";
        var a = Encoding.ASCII.GetBytes(s);
        OK_LEN = (nuint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);
        for (int i=0;i<a.Length;i++) OK_PTR[i]=a[i];
    }

    // ---- helpers (no captures) ----
    static int ParseOne(byte* pbuf, int len)
    {
        for (int i=3;i<len;i++)
            if (pbuf[i-3]=='\r' && pbuf[i-2]=='\n' && pbuf[i-1]=='\r' && pbuf[i]=='\n')
                return i+1;
        return 0;
    }

    static io_uring_sqe* SqeGet(io_uring* pring)
    {
        var sqe = shim_get_sqe(pring);
        if (sqe == null) { shim_submit(pring); sqe = shim_get_sqe(pring); }
        return sqe;
    }

    static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len)
    {
        var sqe = SqeGet(pring);
        shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
    }

    static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid)
    {
        var sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }

    static int CreateListen(string ip, ushort port)
    {
        int lfd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;
        setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int));
        setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int));
        sockaddr_in addr = default;
        addr.sin_family = (ushort)AF_INET;
        addr.sin_port   = Htons(port);
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
        public bool Sending;
        public nuint OutOff, OutLen;
        public byte* OutBuf;
        public Conn(int fd){ Fd=fd; }
    }

    static volatile bool StopAll = false;

    static class Worker
    {
        public static void Run(string ip, ushort port, int workerIndex)
        {
            //Affinity.PinCurrentThreadToCpu(workerIndex % Math.Max(1, Environment.ProcessorCount));

            // Per-worker state
            Conn?[] Conns = new Conn?[MAX_FD];

            // Allocate unmanaged ring + params (no fixed, no captures)
            io_uring* pring   = (io_uring*)NativeMemory.Alloc((nuint)sizeof(io_uring));
            io_uring_params* pprm = (io_uring_params*)NativeMemory.Alloc((nuint)sizeof(io_uring_params));
            *pring = default;
            *pprm  = default;

            // Optional SQPOLL + coop + single-issuer via env
            if (Environment.GetEnvironmentVariable("SQPOLL") == "1")
            {
                pprm->flags = (1u<<3) /*SQPOLL*/ | (1u<<8) /*COOP_TASKRUN*/ | (1u<<12) /*SINGLE_ISSUER*/;
                pprm->sq_thread_idle = 2000;
                // pprm->sq_thread_cpu = (uint)(workerIndex % Math.Max(1, Environment.ProcessorCount));
            }

            int lfd = CreateListen(ip, port);
            int q = shim_queue_init_params((uint)RING_ENTRIES, pring, pprm);
            if (q < 0) { Console.Error.WriteLine($"[w{workerIndex}] queue_init_params: {q}"); return; }

            // Buf-ring (per worker)
            io_uring_buf_ring* BR;
            byte* BR_Slab;
            uint  BR_Mask;
            uint  BR_Idx = 0;

            {
                int ret;
                BR = shim_setup_buf_ring(pring, (uint)BR_ENTRIES, BR_GID, 0, out ret);
                if (BR == null || ret < 0) throw new Exception($"setup_buf_ring failed: ret={ret}, ptr={(nint)BR}");
                BR_Mask = (uint)(BR_ENTRIES - 1);
                nuint slabSize = (nuint)(BR_ENTRIES * RECV_BUF_SZ);
                BR_Slab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);
                for (ushort bid=0; bid<BR_ENTRIES; bid++)
                {
                    byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                    shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, BR_Idx++);
                }
                shim_buf_ring_advance(BR, (uint)BR_ENTRIES);
            }

            // Multishot accept
            {
                var sqe = SqeGet(pring);
                shim_prep_multishot_accept(sqe, lfd, SOCK_NONBLOCK);
                shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, lfd));
                shim_submit(pring);
            }

            var cqes = new io_uring_cqe*[BATCH_CQES];
            int one = 1;

            while (!StopAll)
            {
                int got;
                fixed (io_uring_cqe** pC = cqes)
                    got = shim_peek_batch_cqe(pring, pC, (uint)BATCH_CQES);

                if (got <= 0)
                {
                    io_uring_cqe* oneCqe = null;
                    if (shim_wait_cqe(pring, &oneCqe) != 0) continue;
                    cqes[0] = oneCqe; got = 1;
                }

                for (int i=0;i<got;i++)
                {
                    var cqe = cqes[i];
                    ulong ud = shim_cqe_get_data64(cqe);
                    var kind = UdKindOf(ud);
                    int res  = cqe->res;

                    if (kind == UdKind.Accept)
                    {
                        if (res >= 0)
                        {
                            int fd = res;
                            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));
                            var c = Conns[fd]; if (c is null) Conns[fd] = c = new Conn(fd);
                            c.Sending=false; c.OutBuf=null; c.OutOff=0; c.OutLen=0;
                            ArmRecvMultishot(pring, fd, BR_GID);
                        }
                    }
                    else if (kind == UdKind.Recv)
                    {
                        int fd = UdFdOf(ud);
                        if (res <= 0)
                        {
                            var c = Conns[fd];
                            if (c is not null) { Conns[fd]=null; close(fd); }
                        }
                        else
                        {
                            ushort bid = 0;
                            if (shim_cqe_has_buffer(cqe) != 0) bid = (ushort)shim_cqe_buffer_id(cqe);
                            byte* basePtr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;

                            var c = Conns[fd];
                            if (c is not null)
                            {
                                int usedTotal = 0;
                                while (true)
                                {
                                    int used = ParseOne(basePtr + usedTotal, res - usedTotal);
                                    if (used <= 0) break;
                                    usedTotal += used;
                                    c.OutBuf = OK_PTR; c.OutLen = OK_LEN; c.OutOff = 0;
                                    SubmitSend(pring, c.Fd, c.OutBuf, c.OutOff, c.OutLen);
                                }
                            }

                            // recycle buffer
                            byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                            shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, BR_Idx++);
                            shim_buf_ring_advance(BR, 1);
                        }
                    }
                    else if (kind == UdKind.Send)
                    {
                        int fd = UdFdOf(ud);
                        var c = Conns[fd];
                        if (c is null || res <= 0)
                        {
                            if (c is not null) Conns[fd]=null;
                            close(fd);
                        }
                        else
                        {
                            c.OutOff += (nuint)res;
                            if (c.OutOff < c.OutLen)
                                SubmitSend(pring, c.Fd, c.OutBuf, c.OutOff, c.OutLen);
                            else
                                c.Sending = false;
                        }
                    }

                    shim_cqe_seen(pring, cqe);
                }

                if (shim_sq_ready(pring) > 0) shim_submit(pring);
            }

            close(lfd);

            // Optional frees (process exit will clean up anyway)
            // shim_free_buf_ring(pring, BR, (uint)BR_ENTRIES, BR_GID);
            // NativeMemory.AlignedFree(BR_Slab);
            // NativeMemory.Free(pprm);
            // NativeMemory.Free(pring);
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
