// Program.cs — Single shared io_uring with SQPOLL for all workers
// More reliable SQPOLL implementation with thread-safe ring access

using System.IO;
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
            mask[cpu / 8] |= (byte)(1 << (cpu % 8));
            _ = sched_setaffinity(tid, (nuint)mask.Length, mask);
        }
    }

    // ---- Kernel / Priv helpers ----
    static class Platform
    {
        [DllImport("libc")] static extern uint getuid();
        public static bool IsRoot => getuid() == 0;

        public static bool KernelAtLeast(int major, int minor)
        {
            try
            {
                var rel = File.ReadAllText("/proc/sys/kernel/osrelease");
                var parts = rel.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
                int M = int.Parse(parts[0]);
                int m = int.Parse(parts[1]);
                return (M > major) || (M == major && m >= minor);
            }
            catch { return false; }
        }
    }

    // ---- Shared ring infrastructure ----
    static io_uring* SharedRing;
    static readonly object RingLock = new object();

    static io_uring_sqe* SqeGetThreadSafe(io_uring* pring)
    {
        lock (RingLock)
        {
            var sqe = shim_get_sqe(pring);
            if (sqe == null) { shim_submit(pring); sqe = shim_get_sqe(pring); }
            return sqe;
        }
    }

    static void SubmitThreadSafe(io_uring* pring)
    {
        lock (RingLock)
        {
            if (shim_sq_ready(pring) > 0)
                shim_submit(pring);
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
        for (int i = 0; i < a.Length; i++) OK_PTR[i] = a[i];
    }

    // ---- helpers ----
    static int ParseOne(byte* pbuf, int len)
    {
        for (int i = 3; i < len; i++)
            if (pbuf[i - 3] == '\r' && pbuf[i - 2] == '\n' && pbuf[i - 1] == '\r' && pbuf[i] == '\n')
                return i + 1;
        return 0;
    }

    static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len)
    {
        lock (RingLock)
        {
            var sqe = shim_get_sqe(pring);
            if (sqe == null) { shim_submit(pring); sqe = shim_get_sqe(pring); }
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }
    }

    static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid)
    {
        lock (RingLock)
        {
            var sqe = shim_get_sqe(pring);
            if (sqe == null) { shim_submit(pring); sqe = shim_get_sqe(pring); }
            shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
        }
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
        public bool Sending;
        public nuint OutOff, OutLen;
        public byte* OutBuf;
        public Conn(int fd) { Fd = fd; }
    }

    static volatile bool StopAll = false;

    static class Worker
    {
        public static void Run(int workerIndex, int lfd, io_uring* pring, uint bgid, 
                               io_uring_buf_ring* BR, byte* BR_Slab, uint BR_Mask, uint* BR_Idx)
        {
            //Affinity.PinCurrentThreadToCpu(workerIndex % Math.Max(1, Environment.ProcessorCount));

            // Per-worker connection tracking
            Conn?[] Conns = new Conn?[MAX_FD];

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
                            c.Sending = false; c.OutBuf = null; c.OutOff = 0; c.OutLen = 0;
                            ArmRecvMultishot(pring, fd, bgid);
                        }
                    }
                    else if (kind == UdKind.Recv)
                    {
                        int fd = UdFdOf(ud);
                        if (res <= 0)
                        {
                            var c = Conns[fd];
                            if (c is not null) { Conns[fd] = null; close(fd); }
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

                            // recycle buffer (thread-safe)
                            lock (RingLock)
                            {
                                byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                                shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, *BR_Idx);
                                (*BR_Idx)++;
                                shim_buf_ring_advance(BR, 1);
                            }
                        }
                    }
                    else if (kind == UdKind.Send)
                    {
                        int fd = UdFdOf(ud);
                        var c = Conns[fd];
                        if (c is null || res <= 0)
                        {
                            if (c is not null) Conns[fd] = null;
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

                SubmitThreadSafe(pring);
            }
        }
    }

    static void Main()
    {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        int workers = Math.Max(1, int.TryParse(
            Environment.GetEnvironmentVariable("WORKERS"), out var w) ?
            w : Environment.ProcessorCount);
        workers = 16;

        Console.WriteLine($"Starting {workers} workers with shared ring...");

        // ---- Initialize single shared ring ----
        SharedRing = (io_uring*)NativeMemory.Alloc((nuint)sizeof(io_uring));
        io_uring_params* pprm = (io_uring_params*)NativeMemory.Alloc((nuint)sizeof(io_uring_params));
        *SharedRing = default;
        *pprm = default;

        uint flags = 0;
        bool wantSqpoll = Environment.GetEnvironmentVariable("SQPOLL") == "1";
        
        if (wantSqpoll)
        {
            flags |= IORING_SETUP_SQPOLL;
            if (Platform.KernelAtLeast(5, 19))
                flags |= IORING_SETUP_COOP_TASKRUN;
            if (Platform.KernelAtLeast(6, 0))
                flags |= IORING_SETUP_SINGLE_ISSUER;
            
            pprm->flags = flags;
            pprm->sq_thread_idle = 2000;
            
            var cpuEnv = Environment.GetEnvironmentVariable("SQPOLL_CPU");
            if (!string.IsNullOrEmpty(cpuEnv) && int.TryParse(cpuEnv, out var cpu) && cpu >= 0)
                pprm->sq_thread_cpu = (uint)cpu;
        }

        int q = shim_queue_init_params((uint)RING_ENTRIES, SharedRing, pprm);
        
        if (q < 0)
        {
            const int EINVAL = -22, EPERM = -1, EACCES = -13, EOPNOTSUPP = -95;

            if (q == EINVAL && wantSqpoll && flags != IORING_SETUP_SQPOLL)
            {
                Console.Error.WriteLine($"EINVAL with extra flags; retrying SQPOLL only.");
                pprm->flags = IORING_SETUP_SQPOLL;
                q = shim_queue_init_params((uint)RING_ENTRIES, SharedRing, pprm);
            }

            if ((q == EPERM || q == EACCES) && wantSqpoll)
            {
                Console.Error.WriteLine($"EPERM/EACCES with SQPOLL. Need root or caps. Falling back without SQPOLL.");
                pprm->flags = 0;
                q = shim_queue_init_params((uint)RING_ENTRIES, SharedRing, pprm);
            }
            else if (q == EOPNOTSUPP && wantSqpoll)
            {
                Console.Error.WriteLine($"SQPOLL not supported; falling back.");
                pprm->flags = 0;
                q = shim_queue_init_params((uint)RING_ENTRIES, SharedRing, pprm);
            }
        }

        if (q < 0)
        {
            Console.Error.WriteLine($"Failed to initialize ring: {q}");
            return;
        }

        if ((pprm->flags & IORING_SETUP_SQPOLL) != 0)
        {
            Console.WriteLine($"SQPOLL enabled (flags=0x{pprm->flags:X})");
            if (!Platform.IsRoot)
                Console.Error.WriteLine($"SQPOLL active: ensure caps + memlock (ulimit -l unlimited).");
        }
        else
        {
            Console.WriteLine($"Running without SQPOLL");
        }

        // ---- Create shared buffer ring ----
        io_uring_buf_ring* BR;
        byte* BR_Slab;
        uint BR_Mask;
        uint* BR_Idx = (uint*)NativeMemory.Alloc((nuint)sizeof(uint));
        *BR_Idx = 0;

        {
            int ret;
            BR = shim_setup_buf_ring(SharedRing, (uint)BR_ENTRIES, BR_GID, 0, out ret);
            if (BR == null || ret < 0)
            {
                Console.Error.WriteLine($"setup_buf_ring failed: ret={ret}");
                return;
            }
            BR_Mask = (uint)(BR_ENTRIES - 1);
            nuint slabSize = (nuint)(BR_ENTRIES * RECV_BUF_SZ);
            BR_Slab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);
            for (ushort bid = 0; bid < BR_ENTRIES; bid++)
            {
                byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, *BR_Idx);
                (*BR_Idx)++;
            }
            shim_buf_ring_advance(BR, (uint)BR_ENTRIES);
        }

        // ---- Create listen sockets (one per worker for SO_REUSEPORT) ----
        int[] listenfds = new int[workers];
        for (int i = 0; i < workers; i++)
        {
            listenfds[i] = CreateListen(LISTEN_IP, LISTEN_PORT);
            
            // Arm multishot accept for each listen fd
            lock (RingLock)
            {
                var sqe = shim_get_sqe(SharedRing);
                if (sqe == null) { shim_submit(SharedRing); sqe = shim_get_sqe(SharedRing); }
                shim_prep_multishot_accept(sqe, listenfds[i], SOCK_NONBLOCK);
                shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, listenfds[i]));
            }
        }
        shim_submit(SharedRing);

        Console.WriteLine($"Listening on {LISTEN_IP}:{LISTEN_PORT}");

        // ---- Start worker threads ----
        var threads = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            int wi = i;
            int lfd = listenfds[i];
            threads[i] = new Thread(() =>
            {
                try
                {
                    Worker.Run(wi, lfd, SharedRing, BR_GID, BR, BR_Slab, BR_Mask, BR_Idx);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[w{wi}] crash: {ex}");
                }
            })
            { IsBackground = true, Name = $"uring-w{wi}" };
            threads[i].Start();
        }

        while (!StopAll) Thread.Sleep(250);

        // Cleanup
        foreach (var lfd in listenfds) close(lfd);
        foreach (var t in threads) t.Join();

        Console.WriteLine("Shutting down...");
    }
}