using System.Runtime.CompilerServices;
using Overdrive.Engine;

// dotnet publish -f net9.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program
{
    internal static void Main()
    {
        var builder = OverdriveEngine
            .CreateBuilder()
            .SetWorkersSolver(() => Environment.ProcessorCount / 2)
            .SetBacklog(16 * 1024)
            .SetPort(8080)
            .SetRecvBufferSize(32 * 1024);
        
        var engine = builder.Build();
        engine.Run();
    }
}