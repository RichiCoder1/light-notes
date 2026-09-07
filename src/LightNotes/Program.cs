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
                    builder.Services.AddSingleton<IExternalLinkOpener, SystemExternalLinkOpener>();
                    builder.Services.AddSingleton(TimeProvider.System);
                    builder.Services.AddScoped<IDebounceScheduler>(
                        services => new R3DebounceScheduler(
                            session.Scope,
                            services.GetRequiredService<TimeProvider>()
                        )
                    );
                    builder.Services.AddScoped(services => new NoteWorkspace(
                        session.Scope,
                        Path.Combine(dataDirectory, "notes.db"),
                        services.GetRequiredService<IExternalLinkOpener>(),
                        services.GetRequiredService<IDebounceScheduler>()
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
                .UseWindows(
                    new WindowsWindowOptions
                    {
                        Width = 1180,
                        Height = 760,
                        MinimumWidth = 480,
                        MinimumHeight = 520,
                    }
                )
                .SetTitle("Light Notes")
                .SetTheme(_ => LightNotesTheme.Create())
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
        if (args.Length == 3 && args[0] == "--restore")
        {
            var destinationDirectory = Path.GetFullPath(args[2]);
            if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                throw new IOException("Choose a new, unused directory for the restored workspace.");
            var restoredPath = Path.Combine(destinationDirectory, "notes.db");
            await NoteStore.RestoreAsync(args[1], restoredPath);
            Console.WriteLine("Restored " + restoredPath);
            Console.WriteLine(
                "Set LIGHT_NOTES_DATA_DIRECTORY to " + destinationDirectory + " to open it."
            );
            return 0;
        }
        if (args.Length != 2 || args[0] is not ("--export" or "--backup"))
        {
            Console.Error.WriteLine(
                "Usage: LightNotes [--export <new-file.json> | --backup <new-file.db> | --restore <backup.db> <new-directory>]"
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
