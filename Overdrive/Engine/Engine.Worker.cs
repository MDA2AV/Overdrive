using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using Overdrive.HttpProtocol;
using static Overdrive.ABI.Native;

namespace Overdrive.Engine;

public sealed unsafe partial class OverdriveEngine
{
    // Global pool for Connection objects.
    // NOTE: Pool size is tuned for high-throughput scenarios; adjust as needed.
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024*32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection>
    {
        /// <summary>
        /// Create a new Connection instance with the configured per-worker limits and slab sizes.
        /// </summary>
        public override Connection Create() => new();

        /// <summary>
        /// Return a Connection to the pool. Consider resetting/clearing per-request state here.
        /// </summary>
        public override bool Return(Connection connection)
        {
            // Potentially reset buffers here (e.g., context.Reset()) to avoid data leaks across usages.
            connection.Clear();
            
            return true;
        }
    }

    private static void WorkerLoop(int workerIndex)
    {
        var connections = new Dictionary<int, Connection>(capacity: 1024);
        
        io_uring* pring = null;

        // Buffer-ring resources to ensure we always free them
        io_uring_buf_ring* BR = null;
        byte* BR_Slab = null;
        uint BR_Idx = 0;

        try
        {
            pring = shim_create_ring((uint)s_ringEntries, out int err);
            if (pring == null || err < 0)
            {
                Console.Error.WriteLine($"[w{workerIndex}] create_ring failed: {err}");
                return;
            }

            // Setup buffer ring
            uint BR_Mask;
            {
                BR = shim_setup_buf_ring(pring, (uint)s_bufferRingEntries, c_bufferRingGID, 0, out var ret);
                if (BR == null || ret < 0)
                    throw new Exception($"setup_buf_ring failed: ret={ret}");

                BR_Mask = (uint)(s_bufferRingEntries - 1);
                nuint slabSize = (nuint)(s_bufferRingEntries * s_recvBufferSize);
                BR_Slab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

                for (ushort bid = 0; bid < s_bufferRingEntries; bid++)
                {
                    byte* addr = BR_Slab + (nuint)bid * (nuint)s_recvBufferSize;
                    shim_buf_ring_add(BR, addr, (uint)s_recvBufferSize, bid, (ushort)BR_Mask, BR_Idx++);
                }
                shim_buf_ring_advance(BR, (uint)s_bufferRingEntries);
            }

            var cqes = new io_uring_cqe*[s_batchCQES];
            int connCount = 0;
            var myQueue = WorkerQueues[workerIndex];

            Console.WriteLine($"[w{workerIndex}] Started and ready");

            while (!StopAll)
            {
                // Drain new connections
                int newFds = 0;
                while (myQueue.TryDequeue(out int newFd))
                {
                    connections[newFd] = ConnectionPool.Get().SetFd(newFd);

                    ArmRecvMultishot(pring, newFd, c_bufferRingGID);
                    connCount++;
                    newFds++;
                }

                if (newFds > 0)
                {
                    shim_submit(pring);
                }

                // Process completions
                int got;
                fixed (io_uring_cqe** pC = cqes)
                    got = shim_peek_batch_cqe(pring, pC, (uint)s_batchCQES);

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
                                byte* addr = BR_Slab + (nuint)bid * (nuint)s_recvBufferSize;
                                shim_buf_ring_add(BR, addr, (uint)s_recvBufferSize, bid, (ushort)BR_Mask, BR_Idx++);
                                shim_buf_ring_advance(BR, 1);
                            }

                            if (connections.TryGetValue(fd, out var connection))
                            {
                                ConnectionPool.Return(connection);
                                close(fd);
                                connCount--;
                            }
                        }
                        else
                        {
                            byte* basePtr = BR_Slab + (nuint)bid * (nuint)s_recvBufferSize;

                            //Console.WriteLine(hasMore);
                            //Console.WriteLine($"[w{workerIndex}] FD {fd} request:\n{Encoding.UTF8.GetString(new ReadOnlySpan<byte>(basePtr, res))}");

                            if (connections.TryGetValue(fd, out var connection))
                            {
                                //int usedTotal = 0;
                                while (true) // TODO: THis loop makes no sense
                                {
                                    //int used = ParseOne(basePtr + usedTotal, res - usedTotal);
                                    int used = HeaderParser.FindCrlfCrlf(basePtr, 0, res);
                                    if (used <= 0) break;
                                    //usedTotal += used;

                                    connection.OutPtr = OK_PTR;
                                    connection.OutTail = OK_LEN;
                                    connection.OutHead = 0;

                                    SubmitSend(pring, connection.Fd, connection.OutPtr, connection.OutHead, connection.OutTail);
                                    //processedReqs++;

                                    break;
                                }

                                // Re-arm multishot if terminated
                                if (!hasMore)
                                {
                                    ArmRecvMultishot(pring, fd, c_bufferRingGID);
                                }
                            }

                            // Return buffer after processing
                            byte* addr = BR_Slab + (nuint)bid * (nuint)s_recvBufferSize;
                            shim_buf_ring_add(BR, addr, (uint)s_recvBufferSize, bid, (ushort)BR_Mask, BR_Idx++);
                            shim_buf_ring_advance(BR, 1);
                        }
                    }
                    else if (kind == UdKind.Send)
                    {
                        int fd = UdFdOf(ud);

                        if (connections.TryGetValue(fd, out var connection))
                        {
                            connection.OutHead += (nuint)res;
                            if (connection.OutHead < connection.OutTail)
                                SubmitSend(pring, connection.Fd, connection.OutPtr, connection.OutHead, connection.OutTail);
                            else
                                connection.Sending = false;
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
            CloseAll(connections);

            // Free buffer ring BEFORE destroying the ring
            if (pring != null && BR != null)
            {
                shim_free_buf_ring(pring, BR, (uint)s_bufferRingEntries, c_bufferRingGID);
                BR = null;
            }

            // Destroy ring (unregisters CQ/SQ memory mappings)
            if (pring != null)
            {
                shim_destroy_ring(pring);
                pring = null;
            }

            // Free slab memory used by buf ring
            if (BR_Slab != null)
            {
                NativeMemory.AlignedFree(BR_Slab);
                BR_Slab = null;
            }

            Console.WriteLine($"[w{workerIndex}] Shutdown complete.");
        }
    }
    
    private static void CloseAll(Dictionary<int, Connection> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                close(connection.Value.Fd);
                ConnectionPool.Return(connection.Value);
            }
            catch
            {
                /* ignore */ 
            }
        }
    }
}