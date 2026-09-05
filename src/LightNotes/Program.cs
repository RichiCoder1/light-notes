using Lucent.Hosting;
using Lucent.Platform.Windows;

namespace LightNotes;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var lifecycle = new HostedApplication(
                _ => HostedApplication.CreateBuilder().Build(),
                (_, _) => Components.AppView()
            );
            return LucentApplication
                .CreateBuilder()
                .UseWindows()
                .SetTitle("Light Notes")
                .Build()
                .Run(lifecycle);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Light Notes could not start: {error.Message}");
            return 1;
        }
    }
}
