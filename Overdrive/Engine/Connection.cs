using System.Runtime.CompilerServices;

namespace Overdrive.Engine;

[SkipLocalsInit]
public sealed unsafe class Connection
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
}