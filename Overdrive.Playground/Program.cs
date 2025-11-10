using Overdrive.Engine;

// dotnet publish -f net9.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

internal class Program
{
    internal static void Main()
    {
        var engine = new OverdriveEngine();
        engine.Run();
    }
}