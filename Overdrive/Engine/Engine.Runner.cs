using System.Collections.Concurrent;
using static Overdrive.ABI.Native;

namespace Overdrive.Engine;

public sealed partial class OverdriveEngine
{
    public void Run()
    {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        int workers = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("WORKERS"), out var w) ? w : Environment.ProcessorCount / 2);

        // Create lock-free queues for fd distribution
        WorkerQueues = new ConcurrentQueue<int>[workers];
        WorkerConnectionCounts = new long[workers];
        WorkerRequestCounts = new long[workers];
        
        for (var i = 0; i < workers; i++)
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
                    try { WorkerLoop(wi); }
                    catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
                })
                { IsBackground = true, Name = $"uring-w{wi}" };
            threads[i].Start();
        }

        // Give workers time to initialize
        Thread.Sleep(100);
        
        Console.WriteLine($"Server started with {workers} workers + 1 acceptor");
        
        try
        {
            AcceptorLoop(c_ip, s_port, workers);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[acceptor] crash: {ex}"); 
        }

        // Start acceptor thread
        /*var acceptorThread = new Thread(() =>
        {
            try
            {
                AcceptorLoop(LISTEN_IP, LISTEN_PORT, workers);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[acceptor] crash: {ex}"); 
            }
        })
        {
            IsBackground = true,
            Name = "acceptor" 
        };*/
        
        //acceptorThread.Start();

        //Console.WriteLine($"Server started with {workers} workers + 1 acceptor");

        //while (!StopAll) 
        //    Thread.Sleep(250);

        // Join threads and free global unmanaged buffers
        //acceptorThread.Join();
        foreach (var t in threads) t.Join();
        FreeOk();
    }
}