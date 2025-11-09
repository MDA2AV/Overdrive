using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using static Overdrive.Native;

unsafe class Program
{
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

    internal sealed class Conn
    {
        public int Fd;
        public bool Sending;
        public nuint OutOff, OutLen;
        public byte* OutBuf;

        public Conn(int fd) { Fd = fd; }
    }

    private static volatile bool StopAll = false;

    // Lock-free queues for passing accepted fds to workers
    private static ConcurrentQueue<int>[] WorkerQueues = null!;
    
    // Stats tracking
    private static long[] WorkerConnectionCounts = null!;
    private static long[] WorkerRequestCounts = null!;

    // ============================================================================
    // ACCEPTOR THREAD - Dedicated to accepting connections and load balancing
    // ============================================================================
    internal static class Acceptor
    {
        public static void Run(string ip, ushort port, int workerCount)
        {
            int lfd = CreateListen(ip, port);
            Console.WriteLine($"[acceptor] Listening on {ip}:{port}");
            
            io_uring* pring = shim_create_ring(256, out int err);
            if (pring == null || err < 0)
            {
                Console.Error.WriteLine($"[acceptor] create_ring failed: {err}");
                close(lfd);
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
                            
                            // Set TCP_NODELAY
                            setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));
                            
                            // Round-robin to next worker
                            var targetWorker = nextWorker;
                            nextWorker = (nextWorker + 1) % workerCount;
                            
                            // Enqueue fd to worker
                            WorkerQueues[targetWorker].Enqueue(clientFd);
                            Interlocked.Increment(ref WorkerConnectionCounts[targetWorker]);
                            
                            acceptedTotal++;
                            if (acceptedTotal % 1000 == 0)
                            {
                                Console.WriteLine($"[acceptor] Accepted {acceptedTotal} connections");
                                // Print distribution
                                var distrib = string.Join(", ", WorkerConnectionCounts.Select((c, idx) => $"w{idx}:{c}"));
                                Console.WriteLine($"[acceptor] Distribution: {distrib}");
                            }
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

            close(lfd);
            shim_destroy_ring(pring);
            Console.WriteLine($"[acceptor] Shutdown complete. Total accepted: {acceptedTotal}");
        }
    }

    // ============================================================================
    // WORKER THREAD - Handles I/O for assigned connections
    // ============================================================================
    internal static class Worker
    {
        public static void Run(int workerIndex)
        {
            Conn?[] Conns = new Conn?[MAX_FD];

            io_uring* pring = shim_create_ring((uint)RING_ENTRIES, out int err);
            if (pring == null || err < 0)
            {
                Console.Error.WriteLine($"[w{workerIndex}] create_ring failed: {err}");
                return;
            }

            // Setup buffer ring
            io_uring_buf_ring* BR;
            byte* BR_Slab;
            uint BR_Mask;
            uint BR_Idx = 0;

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
            int loopsSinceWork = 0;
            int newFdsProcessed = 0;

            Console.WriteLine($"[w{workerIndex}] Started and ready");

            while (!StopAll)
            {
                // Check for new connections from acceptor (aggressive polling)
                int newFds = 0;
                while (myQueue.TryDequeue(out int newFd))
                {
                    var c = Conns[newFd];
                    if (c is null) 
                        Conns[newFd] = c = new Conn(newFd);
                    
                    c.Sending = false;
                    c.OutBuf = null;
                    c.OutOff = 0;
                    c.OutLen = 0;
                    
                    ArmRecvMultishot(pring, newFd, BR_GID);
                    connCount++;
                    newFds++;
                    newFdsProcessed++;
                }
                
                if (newFds > 0)
                {
                    // Submit the recv operations immediately
                    shim_submit(pring);
                    if (newFdsProcessed % 1000 == 0)
                    {
                        Console.WriteLine($"[w{workerIndex}] Processed {newFdsProcessed} new connections, active: {connCount}, requests: {processedReqs}");
                    }
                }

                // Update global stats periodically
                Interlocked.Exchange(ref WorkerRequestCounts[workerIndex], processedReqs);

                // Process I/O completions
                int got;
                fixed (io_uring_cqe** pC = cqes)
                    got = shim_peek_batch_cqe(pring, pC, (uint)BATCH_CQES);

                if (got <= 0)
                {
                    // No I/O ready, check if we should wait or spin
                    if (connCount == 0)
                    {
                        // No active connections, just spin
                        Thread.Sleep(1);
                        continue;
                    }
                    
                    // We have connections, wait for I/O
                    io_uring_cqe* oneCqe = null;
                    if (shim_wait_cqe(pring, &oneCqe) != 0) 
                        continue;
                    
                    cqes[0] = oneCqe;
                    got = 1;
                }

                loopsSinceWork = 0;

                for (int i = 0; i < got; i++)
                {
                    var cqe = cqes[i];
                    ulong ud = shim_cqe_get_data64(cqe);
                    var kind = UdKindOf(ud);
                    int res = cqe->res;

                    if (kind == UdKind.Recv)
                    {
                        int fd = UdFdOf(ud);
                        if (res <= 0)
                        {
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
                            ushort bid = 0;
                            if (shim_cqe_has_buffer(cqe) != 0) 
                                bid = (ushort)shim_cqe_buffer_id(cqe);
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
                                    
                                    c.OutBuf = OK_PTR;
                                    c.OutLen = OK_LEN;
                                    c.OutOff = 0;
                                    
                                    SubmitSend(pring, c.Fd, c.OutBuf, c.OutOff, c.OutLen);
                                    processedReqs++;
                                }
                            }

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

            shim_destroy_ring(pring);
            Console.WriteLine($"[w{workerIndex}] Shutdown. Connections: {connCount}, Requests: {processedReqs}");
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

        // Stats printer thread
        /*var statsThread = new Thread(() =>
        {
            while (!StopAll)
            {
                Thread.Sleep(5000); // Print every 5 seconds
                if (StopAll) break;
                
                var connStats = string.Join(", ", WorkerConnectionCounts.Select((c, idx) => $"w{idx}:{c}"));
                var reqStats = string.Join(", ", WorkerRequestCounts.Select((r, idx) => $"w{idx}:{r}"));
                Console.WriteLine($"[stats] Connections assigned: {connStats}");
                Console.WriteLine($"[stats] Requests processed:   {reqStats}");
            }
        })
        { IsBackground = true, Name = "stats" };
        statsThread.Start();*/

        while (!StopAll) Thread.Sleep(250);
        
        acceptorThread.Join();
        foreach (var t in threads) t.Join();
    }
}