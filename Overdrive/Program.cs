using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using System.Linq;
using Overdrive.HttpProtocol; // for Select used in logs
using static Overdrive.Native;

unsafe class Program
{
    // io_uring completion flags
    private const uint IORING_CQE_F_MORE = (1U << 1);

    private static byte* OK_PTR;
    private static nuint OK_LEN;

    private static void InitOk()
    {
        var s = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nHello, World!";
        var a = Encoding.UTF8.GetBytes(s);
        OK_LEN = (nuint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);
        for (int i = 0; i < a.Length; i++)
            OK_PTR[i] = a[i];
    }

    private static void FreeOk()
    {
        if (OK_PTR != null)
        {
            NativeMemory.Free(OK_PTR);
            OK_PTR = null;
            OK_LEN = 0;
        }
    }

    private static int ParseOne(byte* pbuf, int len)
    {
        for (int i = 3; i < len; i++)
            if (pbuf[i - 3] == '\r' && pbuf[i - 2] == '\n' && pbuf[i - 1] == '\r' && pbuf[i] == '\n')
                return i + 1;
        return 0;
    }

    private static io_uring_sqe* SqeGet(io_uring* pring)
    {
        var sqe = shim_get_sqe(pring);
        if (sqe == null) { shim_submit(pring); sqe = shim_get_sqe(pring); }
        return sqe;
    }

    private static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len)
    {
        var sqe = SqeGet(pring);
        shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
    }

    private static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid)
    {
        var sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }

    private static int CreateListen(string ip, ushort port)
    {
        int lfd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;

        setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int));
        setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family = (ushort)AF_INET;
        addr.sin_port = Htons(port);

        var ipb = Encoding.UTF8.GetBytes(ip + "\0");
        fixed (byte* pip = ipb)
            inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);

        bind(lfd, &addr, (uint)sizeof(sockaddr_in));
        listen(lfd, BACKLOG);

        int fl = fcntl(lfd, F_GETFL, 0);
        fcntl(lfd, F_SETFL, fl | O_NONBLOCK);

        return lfd;
    }

    internal sealed class Connection
    {
        public int Fd;
        public bool Sending;
        
        // Out buffer
        public nuint OutHead, OutTail;
        public byte* OutPtr;

        public Connection(int fd)
        {
            Fd = fd; 
        }
    }

    private static volatile bool StopAll = false;

    // Lock-free queues for passing accepted fds to workers
    private static ConcurrentQueue<int>[] WorkerQueues = null!;

    // Stats tracking
    private static long[] WorkerConnectionCounts = null!;
    private static long[] WorkerRequestCounts = null!;

    // ============================================================================
    // ACCEPTOR THREAD
    // ============================================================================
    internal static class Acceptor
    {
        public static void Run(string ip, ushort port, int workerCount)
        {
            int lfd = CreateListen(ip, port);
            io_uring* pring = null;
            try
            {
                Console.WriteLine($"[acceptor] Listening on {ip}:{port}");

                pring = shim_create_ring(256, out int err);
                if (pring == null || err < 0)
                {
                    Console.Error.WriteLine($"[acceptor] create_ring failed: {err}");
                    return;
                }

                // Start multishot accept
                var sqe = SqeGet(pring);
                shim_prep_multishot_accept(sqe, lfd, SOCK_NONBLOCK);
                shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, lfd));
                shim_submit(pring);

                Console.WriteLine("[acceptor] Multishot accept armed");

                var cqes = new io_uring_cqe*[32];
                int nextWorker = 0;
                int one = 1;
                long acceptedTotal = 0;

                Console.WriteLine($"[acceptor] Load balancing across {workerCount} workers");

                while (!StopAll)
                {
                    int got;
                    fixed (io_uring_cqe** pC = cqes)
                        got = shim_peek_batch_cqe(pring, pC, (uint)cqes.Length);

                    if (got <= 0)
                    {
                        io_uring_cqe* oneCqe = null;
                        if (shim_wait_cqe(pring, &oneCqe) != 0) continue;
                        cqes[0] = oneCqe;
                        got = 1;
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
                                int clientFd = res;

                                // TCP_NODELAY
                                setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                                // Round-robin to next worker
                                var targetWorker = nextWorker;
                                nextWorker = (nextWorker + 1) % workerCount;

                                WorkerQueues[targetWorker].Enqueue(clientFd);
                                //Interlocked.Increment(ref WorkerConnectionCounts[targetWorker]);

                                /*acceptedTotal++;
                                if (acceptedTotal % 1000 == 0)
                                {
                                    Console.WriteLine($"[acceptor] Accepted {acceptedTotal} connections");
                                    var distrib = string.Join(", ", WorkerConnectionCounts.Select((c, idx) => $"w{idx}:{c}"));
                                    Console.WriteLine($"[acceptor] Distribution: {distrib}");
                                }*/
                            }
                            else
                            {
                                Console.WriteLine($"[acceptor] Accept error: {res}");
                            }
                        }

                        shim_cqe_seen(pring, cqe);
                    }

                    if (shim_sq_ready(pring) > 0) shim_submit(pring);
                }
            }
            finally
            {
                // close listener and ring even on exception/StopAll
                if (lfd >= 0) 
                    close(lfd);
                
                if (pring != null) 
                    shim_destroy_ring(pring);
                
                Console.WriteLine($"[acceptor] Shutdown complete.");
            }
        }
    }

    // ============================================================================
    // WORKER THREAD
    // ============================================================================
    internal static class Worker
    {
        public static void Run(int workerIndex)
        {
            Connection?[] Conns = new Connection?[MAX_FD];
            io_uring* pring = null;

            // Buffer-ring resources to ensure we always free them
            io_uring_buf_ring* BR = null;
            byte* BR_Slab = null;
            uint BR_Mask = 0;
            uint BR_Idx = 0;

            try
            {
                pring = shim_create_ring((uint)RING_ENTRIES, out int err);
                if (pring == null || err < 0)
                {
                    Console.Error.WriteLine($"[w{workerIndex}] create_ring failed: {err}");
                    return;
                }

                // Setup buffer ring
                {
                    BR = shim_setup_buf_ring(pring, (uint)BR_ENTRIES, BR_GID, 0, out var ret);
                    if (BR == null || ret < 0)
                        throw new Exception($"setup_buf_ring failed: ret={ret}");

                    BR_Mask = (uint)(BR_ENTRIES - 1);
                    nuint slabSize = (nuint)(BR_ENTRIES * RECV_BUF_SZ);
                    BR_Slab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

                    for (ushort bid = 0; bid < BR_ENTRIES; bid++)
                    {
                        byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                        shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, BR_Idx++);
                    }
                    shim_buf_ring_advance(BR, (uint)BR_ENTRIES);
                }

                var cqes = new io_uring_cqe*[BATCH_CQES];
                int connCount = 0;
                var myQueue = WorkerQueues[workerIndex];
                long processedReqs = 0;
                int newFdsProcessed = 0;

                Console.WriteLine($"[w{workerIndex}] Started and ready");

                while (!StopAll)
                {
                    // Drain new connections
                    int newFds = 0;
                    while (myQueue.TryDequeue(out int newFd))
                    {
                        var c = Conns[newFd];
                        if (c is null)
                            Conns[newFd] = c = new Connection(newFd);

                        c.Sending = false;
                        c.OutPtr = null;
                        c.OutHead = 0;
                        c.OutTail = 0;

                        ArmRecvMultishot(pring, newFd, BR_GID);
                        connCount++;
                        newFds++;
                        newFdsProcessed++;
                    }

                    if (newFds > 0)
                    {
                        shim_submit(pring);
                        /*if (newFdsProcessed % 1000 == 0)
                        {
                            Console.WriteLine($"[w{workerIndex}] Processed {newFdsProcessed} new connections, active: {connCount}, requests: {processedReqs}");
                        }*/
                    }

                    // Update global stats
                    //Interlocked.Exchange(ref WorkerRequestCounts[workerIndex], processedReqs);

                    // Process completions
                    int got;
                    fixed (io_uring_cqe** pC = cqes)
                        got = shim_peek_batch_cqe(pring, pC, (uint)BATCH_CQES);

                    if (got <= 0)
                    {
                        if (connCount == 0)
                        {
                            Thread.Sleep(1);
                            continue;
                        }

                        io_uring_cqe* oneCqe = null;
                        if (shim_wait_cqe(pring, &oneCqe) != 0)
                            continue;

                        cqes[0] = oneCqe;
                        got = 1;
                    }

                    for (int i = 0; i < got; i++)
                    {
                        var cqe = cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        var kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Recv)
                        {
                            int fd = UdFdOf(ud);
                            ushort bid = 0;
                            bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                            bool hasMore = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (hasBuffer)
                                bid = (ushort)shim_cqe_buffer_id(cqe);

                            if (res <= 0)
                            {
                                // Return buffer BEFORE closing connection
                                if (hasBuffer)
                                {
                                    byte* addr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;
                                    shim_buf_ring_add(BR, addr, (uint)RECV_BUF_SZ, bid, (ushort)BR_Mask, BR_Idx++);
                                    shim_buf_ring_advance(BR, 1);
                                }

                                var c = Conns[fd];
                                if (c is not null)
                                {
                                    Conns[fd] = null;
                                    close(fd);
                                    connCount--;
                                }
                            }
                            else
                            {
                                // DIOGO HERE, next actions is to have the receive slab point to a buffer in the connection object instead
                                // of a big slab split by indexes?
                                
                                // TODO: This receive loop must be improved a lot
                                
                                byte* basePtr = BR_Slab + (nuint)bid * (nuint)RECV_BUF_SZ;

                                //Console.WriteLine(hasMore);
                                //Console.WriteLine($"[w{workerIndex}] FD {fd} request:\n{Encoding.UTF8.GetString(new ReadOnlySpan<byte>(basePtr, res))}");

                                var c = Conns[fd];
                                if (c is not null)
                                {
                                    //int usedTotal = 0;
                                    while (true) // TODO: THis loop makes no sense
                                    {
                                        //int used = ParseOne(basePtr + usedTotal, res - usedTotal);
                                        int used = HeaderParser.FindCrlfCrlf(basePtr, 0, res);
                                        if (used <= 0) break;
                                        //usedTotal += used;

                                        c.OutPtr = OK_PTR;
                                        c.OutTail = OK_LEN;
                                        c.OutHead = 0;

                                        SubmitSend(pring, c.Fd, c.OutPtr, c.OutHead, c.OutTail);
                                        processedReqs++;

                                        break;
                                    }

                                    // Re-arm multishot if terminated
                                    if (!hasMore)
                                    {
                                        ArmRecvMultishot(pring, fd, BR_GID);
                                    }
                                }

                                // Return buffer after processing
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
                                if (c is not null)
                                {
                                    Conns[fd] = null;
                                    connCount--;
                                }
                                close(fd);
                            }
                            else
                            {
                                c.OutHead += (nuint)res;
                                if (c.OutHead < c.OutTail)
                                    SubmitSend(pring, c.Fd, c.OutPtr, c.OutHead, c.OutTail);
                                else
                                    c.Sending = false;
                            }
                        }

                        shim_cqe_seen(pring, cqe);
                    }

                    if (shim_sq_ready(pring) > 0) shim_submit(pring);
                }
            }
            finally
            {
                // Close any remaining connections
                CloseAll(Conns);

                // Tear down ring first (unregisters resources)
                if (pring != null) shim_destroy_ring(pring);

                // Free the slab we allocated
                if (BR_Slab != null)
                {
                    NativeMemory.AlignedFree(BR_Slab);
                    BR_Slab = null;
                }

                Console.WriteLine($"[w{workerIndex}] Shutdown complete.");
            }
        }
        
        

        private static void CloseAll(Connection?[] conns)
        {
            for (int i = 0; i < conns.Length; i++)
            {
                var c = conns[i];
                if (c is not null)
                {
                    try { close(c.Fd); } catch { /* ignore */ }
                    conns[i] = null;
                }
            }
        }
    }

    // ============================================================================
    // MAIN
    // ============================================================================
    internal static void Main()
    {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        int workers = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("WORKERS"), out var w) ? w : Environment.ProcessorCount / 2);

        // Create lock-free queues for fd distribution
        WorkerQueues = new ConcurrentQueue<int>[workers];
        WorkerConnectionCounts = new long[workers];
        WorkerRequestCounts = new long[workers];
        for (int i = 0; i < workers; i++)
        {
            WorkerQueues[i] = new ConcurrentQueue<int>();
            WorkerConnectionCounts[i] = 0;
            WorkerRequestCounts[i] = 0;
        }

        // Start worker threads FIRST
        var threads = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            int wi = i;
            threads[i] = new Thread(() =>
            {
                try { Worker.Run(wi); }
                catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
            })
            { IsBackground = true, Name = $"uring-w{wi}" };
            threads[i].Start();
        }

        // Give workers time to initialize
        Thread.Sleep(100);

        // Start acceptor thread
        var acceptorThread = new Thread(() =>
        {
            try { Acceptor.Run(LISTEN_IP, LISTEN_PORT, workers); }
            catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        })
        { IsBackground = true, Name = "acceptor" };
        acceptorThread.Start();

        Console.WriteLine($"Server started with {workers} workers + 1 acceptor");

        while (!StopAll) Thread.Sleep(250);

        // Join threads and free global unmanaged buffers
        acceptorThread.Join();
        foreach (var t in threads) t.Join();
        FreeOk();
    }
}
