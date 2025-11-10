namespace Overdrive.Engine;

public sealed unsafe partial class OverdriveEngine
{
    private const int c_bufferRingGID       = 1;
    
    private const string c_ip = "0.0.0.0";
    
    // ---- Tunables ----
    private static int s_ringEntries = 8192;
    private static int s_recvBufferSize  = 32 * 1024;
    private static int s_bufferRingEntries   = 4096;     // power-of-two
    private static int s_backlog      = 65535;
    private static int s_batchCQES   = 512;
    
    private static ushort s_port = 8080;
    
    public sealed class OverdriveBuilder
    {
        private readonly OverdriveEngine _engine;
        
        public OverdriveBuilder() => _engine = new OverdriveEngine();

        public OverdriveBuilder SetBacklog(int backlog)
        {
            s_backlog = backlog;
            
            return this;
        }
        
        public OverdriveBuilder SetPort(ushort port)
        {
            s_port = port;
            
            return this;
        }
        
        
        
    }
}