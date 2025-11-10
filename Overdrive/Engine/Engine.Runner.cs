using System.Collections.Concurrent;
using static Overdrive.ABI.Native;

namespace Overdrive.Engine;

public sealed partial class OverdriveEngine
{
    private static volatile bool StopAll = false;

    // Lock-free queues for passing accepted fds to workers
    private static ConcurrentQueue<int>[] WorkerQueues = null!;

    // Stats tracking
    private static long[] WorkerConnectionCounts = null!;
    private static long[] WorkerRequestCounts = null!;
    
    public void Run()
    {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        // Create lock-free queues for fd distribution
        WorkerQueues = new ConcurrentQueue<int>[s_nWorkers];
        WorkerConnectionCounts = new long[s_nWorkers];
        WorkerRequestCounts = new long[s_nWorkers];
        
        for (var i = 0; i < s_nWorkers; i++)
        {
            WorkerQueues[i] = new ConcurrentQueue<int>();
            WorkerConnectionCounts[i] = 0;
            WorkerRequestCounts[i] = 0;
        }

        // Start worker threads FIRST
        var threads = new Thread[s_nWorkers];
        for (int i = 0; i < s_nWorkers; i++)
        {
            int wi = i;
            threads[i] = new Thread(() =>
                {
                    try { WorkerLoop(wi); }
                    catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
                })
                { IsBackground = true, Name = $"uring-w{wi}" };
            threads[i].Start();
        }

        // Give workers time to initialize
        Thread.Sleep(100);
        
        Console.WriteLine($"Server started with {s_nWorkers} workers + 1 acceptor");
        
        try
        {
            AcceptorLoop(c_ip, s_port, s_nWorkers);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[acceptor] crash: {ex}"); 
        }

        foreach (var t in threads) t.Join();
        FreeOk();
    }
}