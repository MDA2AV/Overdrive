using System.Runtime.InteropServices;
using System.Text;
using static Overdrive.Native;

unsafe class Program
{
    // ---- Unmanaged OK buffer (shared) ----
    private static byte* OK_PTR; static nuint OK_LEN;
    
    private static void InitOk()
    {
        var s = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nHello, World!";
        var a = Encoding.UTF8.GetBytes(s);
        
        OK_LEN = (nuint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);
        
        for (int i = 0; i < a.Length; i++) 
            OK_PTR[i] = a[i];
    }

    // ---- helpers (no captures) ----
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

        public Conn(int fd)
        {
            Fd = fd; 
        }
    }

    private static volatile bool StopAll = false;

    internal static class Worker
    {
        public static void Run(string ip, ushort port, int workerIndex)
        {
            //Affinity.PinCurrentThreadToCpu(workerIndex % Math.Max(1, Environment.ProcessorCount));

            // Per-worker state
            // TODO: Improve Conn class
            // TODO: Store conns in a dictionary where key is the fd
            Conn?[] Conns = new Conn?[MAX_FD];

            // Create listening socket
            int lfd = CreateListen(ip, port);

            // Create ring via shim (no params/SQPOLL in managed code)
            io_uring* pring = shim_create_ring((uint)RING_ENTRIES, out int err);
            
            if (pring == null || err < 0)
            {
                Console.Error.WriteLine($"[w{workerIndex}] create_ring failed: {err}");
                close(lfd);
                return;
            }

            // Buf-ring (per worker)
            io_uring_buf_ring* BR;
            byte* BR_Slab;
            uint BR_Mask;
            uint BR_Idx = 0;

            {
                BR = shim_setup_buf_ring(pring, (uint)BR_ENTRIES, BR_GID, 0, out var ret);
                
                if (BR == null || ret < 0) 
                    throw new Exception($"setup_buf_ring failed: ret={ret}, ptr={(nint)BR}");
                
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

            // Multishot accept
            {
                var sqe = SqeGet(pring);
                shim_prep_multishot_accept(sqe, lfd, SOCK_NONBLOCK);
                shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, lfd));
                shim_submit(pring);
            }
            
            // TODO: Consider using a marshall unmanaged pinned array here?
            var cqes = new io_uring_cqe*[BATCH_CQES]; // Array of native pointers to CQE
            int one = 1;

            while (!StopAll)
            {
                int got;
                fixed (io_uring_cqe** pC = cqes)      // Pointer to array of pointers to CQE
                    got = shim_peek_batch_cqe(pring, pC, (uint)BATCH_CQES); // tries to read up to BATCH_CQES completion events from the ring without blocking

                /*This block runs when peek_batch_cqe() found no ready completion events (got <= 0).
                 * In that case, we need to wait for the next event, so we declare a local pointer
                 * variable oneCqe which will hold a pointer to a CQE stored inside the ring.
                 * We then call shim_wait_cqe(pring, &oneCqe), passing the address of that pointer variable,
                 * which allows the kernel to write the actual CQE pointer into it. If shim_wait_cqe returns non-zero,
                 * we skip this loop iteration. Otherwise, we store the returned CQE pointer into cqes[0] and set got = 1,
                 * so the rest of the event-processing loop can handle it exactly the same way as a batched completion.
                 * This ensures a smooth transition between the fast path (batch peek) and the slow path (blocking wait),
                 * while keeping downstream logic uniform.*/
                if (got <= 0) // Found 0 CQEs, no events are ready right now
                {
                    
                    // Declare a pointer variable that will receive the CQE pointer.
                    // The CQE itself is stored inside the ring; we only receive a pointer to it.
                    io_uring_cqe* oneCqe = null;
                    
                    if (shim_wait_cqe(pring, &oneCqe) != 0) // & oneCqe is not a pointer to a pointer, io_uring_cqe**
                        continue;
                    
                    cqes[0] = oneCqe; 
                    got = 1;
                }
                
                // For each completion event that we have ready to handle, process it.
                for (int i = 0; i < got; i++)
                {
                    /*cqe is the pointer to the i-th completion event we’re handling.
                       shim_cqe_get_data64(cqe) retrieves the 64-bit user-data value we attached when we originally submitted the operation.
                       We encoded both what kind of operation it was (Accept / Recv / Send) and which socket fd it belongs to inside that user-data.
                       
                       UdKindOf(ud) extracts the operation type from that bit-packed value — so we know which handler to run.
                       cqe->res is the result of the syscall, meaning:
                        - For Accept: the return value is the new client file descriptor, or negative if error.
                        - For Recv: it’s number of bytes received, or 0 / <0 on disconnect/error.
                        - For Send: it’s number of bytes written, or <=0 if sending failed.*/
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
                            
                            var c = Conns[fd]; 
                            if (c is null) 
                                Conns[fd] = c = new Conn(fd);
                            
                            c.Sending = false; 
                            c.OutBuf = null; 
                            c.OutOff = 0; 
                            c.OutLen = 0;
                            
                            ArmRecvMultishot(pring, fd, BR_GID);
                        }
                    }
                    else if (kind == UdKind.Recv)
                    {
                        int fd = UdFdOf(ud);
                        if (res <= 0)
                        {
                            var c = Conns[fd];
                            if (c is not null)
                            {
                                Conns[fd] = null; 
                                close(fd); 
                            }
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
                                    
                                    c.OutBuf = OK_PTR; 
                                    c.OutLen = OK_LEN; 
                                    c.OutOff = 0;
                                    
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

                if (shim_sq_ready(pring) > 0) shim_submit(pring);
            }

            close(lfd);

            // Optional frees (process exit will clean up anyway)
            // shim_free_buf_ring(pring, BR, (uint)BR_ENTRIES, BR_GID);
            // NativeMemory.AlignedFree(BR_Slab);

            shim_destroy_ring(pring);
        }
    }

    internal static void Main()
    {
        // dotnet publish -f net9.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed
        
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        int workers = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("WORKERS"), out var w) ? w : Environment.ProcessorCount / 2);

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
