using LightNotes.Storage;
using Lucent.Core;
using Lucent.Hosting;
using Lucent.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace LightNotes;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var dataDirectory =
                Environment.GetEnvironmentVariable("LIGHT_NOTES_DATA_DIRECTORY")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LightNotes"
                );
            if (args.Length != 0)
                return MaintainAsync(args, Path.Combine(dataDirectory, "notes.db"))
                    .GetAwaiter()
                    .GetResult();
            var lifecycle = new HostedApplication(
                session =>
                {
                    var builder = HostedApplication.CreateBuilder();
                    builder.Services.AddScoped(_ => new NoteWorkspace(
                        session.Scope,
                        Path.Combine(dataDirectory, "notes.db")
                    ));
                    return builder.Build();
                },
                (services, session) =>
                {
                    var workspace = services.GetRequiredService<NoteWorkspace>();
                    _ = workspace.StartAsync();
                    return Components.AppView(workspace);
                },
                services => services.GetRequiredService<NoteWorkspace>().PrepareCloseAsync()
            );
            return LucentApplication
                .CreateBuilder()
                .UseWindows()
                .SetTitle("Light Notes")
                .SetTheme(_ => ControlThemes.Light)
                .Build()
                .Run(lifecycle);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Light Notes could not start: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> MaintainAsync(string[] args, string databasePath)
    {
        if (args.Length != 2 || args[0] is not ("--export" or "--backup"))
        {
            Console.Error.WriteLine(
                "Usage: LightNotes [--export <new-file.json> | --backup <new-file.db>]"
            );
            return 2;
        }
        await using var store = await NoteStore.OpenAsync(databasePath);
        if (args[0] == "--export")
            await store.ExportJsonAsync(args[1]);
        else
            await store.BackupAsync(args[1]);
        Console.WriteLine("Created " + Path.GetFullPath(args[1]));
        return 0;
    }
}
