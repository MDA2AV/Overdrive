using System.Runtime.CompilerServices;
using Overdrive.HttpProtocol;

namespace Overdrive.Engine;

[SkipLocalsInit]
public sealed unsafe class Connection : IDisposable
{
    public int Fd;
    public bool Sending;
    
    // In buffers
    
        
    // Out buffer
    public nuint OutHead, OutTail;
    public byte* OutPtr;
    
    public H1HeaderData H1HeaderData { get; set; } = null!;

    public Connection(int fd)
    {
        Fd = fd; 
    }

    public Connection()
    {
    }

    public void Clear()
    {
        Sending = false;
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;
    }

    public Connection SetFd(int fd)
    {
        Fd = fd;
            
        return this;
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }
}