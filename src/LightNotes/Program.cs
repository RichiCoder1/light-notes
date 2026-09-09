using System.Globalization;
using System.Reflection;
using System.Text;
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
        var dataDirectory =
            Environment.GetEnvironmentVariable("LIGHT_NOTES_DATA_DIRECTORY")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LightNotes"
            );
        AppDomain.CurrentDomain.UnhandledException += (_, unhandled) =>
        {
            if (unhandled.ExceptionObject is Exception error)
                TryWriteCrashReport(dataDirectory, error);
        };
        try
        {
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
                (services, cancellationToken) =>
                    services
                        .GetRequiredService<NoteWorkspace>()
                        .PrepareCloseAsync(cancellationToken)
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
                .SetFailureReporter(report => ReportApplicationFailure(dataDirectory, report))
                .SetTheme(_ => LightNotesTheme.Create())
                .Build()
                .Run(lifecycle);
        }
        catch (Exception error)
        {
            var reportPath = TryWriteCrashReport(dataDirectory, error);
            Console.Error.WriteLine(
                $"Light Notes encountered an error: {error.Message}"
                    + (reportPath is null ? "" : $" Crash details: {reportPath}")
            );
            return 1;
        }
    }

    private static void ReportApplicationFailure(
        string dataDirectory,
        ApplicationFailureReport report
    )
    {
        try
        {
            var reportPath = TryWriteCrashReport(dataDirectory, report.Error);
            Console.Error.WriteLine(
                $"Light Notes encountered a {report.Kind} failure: {report.Error.Message}"
                    + (reportPath is null ? "" : $" Crash details: {reportPath}")
            );
        }
        catch
        {
            // Failure reporting must not turn a late cleanup fault into another process failure.
        }
    }

    internal static string? TryWriteCrashReport(string dataDirectory, Exception error)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            ArgumentNullException.ThrowIfNull(error);
            Directory.CreateDirectory(dataDirectory);
            var builder = new StringBuilder(4096);
            builder.AppendLine("Light Notes crash");
            builder
                .Append("App: ")
                .AppendLine(
                    typeof(Program)
                        .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion
                        ?? "unknown"
                );
            builder.Append("UTC: ").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
            builder.Append("Runtime: ").AppendLine(Environment.Version.ToString());
            var current = error;
            for (var depth = 0; current is not null && depth < 8; depth++)
            {
                builder
                    .Append("Exception ")
                    .Append(depth)
                    .Append(": ")
                    .Append(current.GetType().FullName)
                    .Append(" (0x")
                    .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                    .AppendLine(")");
                if (current.StackTrace is { Length: > 0 } stack)
                    builder.AppendLine(stack);
                current = current.InnerException;
            }
            if (current is not null)
                builder.AppendLine("Additional inner exceptions omitted.");

            const int maximumCharacters = 64 * 1024;
            var report = builder.ToString();
            if (report.Length > maximumCharacters)
            {
                var suffix = Environment.NewLine + "Report truncated.";
                report = report[..(maximumCharacters - suffix.Length)] + suffix;
            }
            var path = Path.Combine(dataDirectory, "last-crash.txt");
            File.WriteAllText(path, report, Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
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
