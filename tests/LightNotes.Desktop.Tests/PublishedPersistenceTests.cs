using System.Diagnostics;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;

namespace LightNotes.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class PublishedPersistenceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void PublishedCaptureAutosaveAndReopenRetainsTheEditedNote()
    {
        var dataDirectory = CreateTestDataDirectory();
        try
        {
            using (var process = StartApplication(dataDirectory))
            {
                try
                {
                    var window = WaitForWindow(process);
                    using var automation = new UIA3Automation();
                    var root = automation.FromHandle(window);
                    WaitForInitialStatus(process, root);

                    var capture = FindByName(root, ControlType.Edit, "Capture a link or thought");
                    BringToForeground(process, window);
                    capture.Focus();
                    Keyboard.Type("Persisted desktop note");
                    Wait.UntilInputIsProcessed();
                    WaitUntil(
                        process,
                        () =>
                            TryReadValue(root, "Capture a link or thought", out var currentCapture)
                            && currentCapture == "Persisted desktop note",
                        "The capture field rejected physical keyboard input."
                    );
                    InvokeButton(process, root, "Add");
                    WaitForStatus(process, root, "Saved on this device");

                    var row = WaitForElement(
                        process,
                        root,
                        () =>
                            root.FindFirstDescendant(condition =>
                                condition
                                    .ByControlType(ControlType.ListItem)
                                    .And(
                                        condition.ByName(
                                            "Persisted desktop note",
                                            FlaUI
                                                .Core
                                                .Definitions
                                                .PropertyConditionFlags
                                                .MatchSubstring
                                        )
                                    )
                            ),
                        "The captured note did not appear in the inbox."
                    );
                    Assert.IsNotNull(row, "The captured note row was not exposed through UIA.");

                    SetFieldValue(process, root, "Title", "Persisted title");
                    SetFieldValue(process, root, "Notes", "Persisted body");
                    // Prove autosave committed while the app is open; close-time flush must not satisfy this check.
                    WaitUntil(
                        process,
                        () => HasPersistedDraft(dataDirectory, "Persisted title", "Persisted body"),
                        "Autosave did not commit the edited draft before closing."
                    );
                    WaitForStatus(process, root, "Saved on this device");

                    var artifactRoot =
                        Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                        ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "desktop");
                    Directory.CreateDirectory(artifactRoot);
                    BringToForeground(process, window);
                    Wait.UntilInputIsProcessed();
                    Capture
                        .Element(root)
                        .ToFile(Path.Combine(artifactRoot, "light-notes-persistence.png"));

                    Assert.IsTrue(
                        process.CloseMainWindow(),
                        "The first Light Notes process did not accept an ordinary close."
                    );
                    WaitUntil(
                        process,
                        () => process.HasExited,
                        "The first Light Notes process did not close cleanly."
                    );
                    Assert.AreEqual(
                        0,
                        process.ExitCode,
                        "The first Light Notes process exited unsuccessfully."
                    );
                }
                finally
                {
                    StopApplication(process);
                }
            }

            using (var reopened = StartApplication(dataDirectory))
            {
                try
                {
                    var window = WaitForWindow(reopened);
                    using var automation = new UIA3Automation();
                    var root = automation.FromHandle(window);
                    WaitForStatus(reopened, root, "Saved on this device");
                    WaitUntil(
                        reopened,
                        () =>
                            TryReadValue(root, "Title", out var title)
                            && title == "Persisted title",
                        "The edited title was not restored after reopening."
                    );
                    WaitUntil(
                        reopened,
                        () => TryReadValue(root, "Notes", out var body) && body == "Persisted body",
                        "The edited body was not restored after reopening."
                    );

                    Assert.IsTrue(
                        reopened.CloseMainWindow(),
                        "The reopened Light Notes process did not accept an ordinary close."
                    );
                    WaitUntil(
                        reopened,
                        () => reopened.HasExited,
                        "The reopened Light Notes process did not close cleanly."
                    );
                    Assert.AreEqual(
                        0,
                        reopened.ExitCode,
                        "The reopened Light Notes process exited unsuccessfully."
                    );
                }
                finally
                {
                    StopApplication(reopened);
                }
            }
        }
        finally
        {
            DeleteTestDataDirectory(dataDirectory);
        }
    }

    private static string CreateTestDataDirectory()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.GetFullPath(
            Path.Combine(tempRoot, "light-notes-desktop-" + Guid.NewGuid().ToString("N"))
        );
        EnsureWithinTempRoot(tempRoot, dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static void DeleteTestDataDirectory(string dataDirectory)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        EnsureWithinTempRoot(tempRoot, dataDirectory);
        if (Directory.Exists(dataDirectory))
        {
            var crashReport = Path.Combine(dataDirectory, "last-crash.txt");
            if (File.Exists(crashReport))
            {
                var artifacts =
                    Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "desktop");
                Directory.CreateDirectory(artifacts);
                File.Copy(
                    crashReport,
                    Path.Combine(artifacts, Path.GetFileName(dataDirectory) + "-crash.txt")
                );
            }
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static void EnsureWithinTempRoot(string tempRoot, string directory)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(tempRoot),
            Path.GetFullPath(directory)
        );
        if (
            relative == "."
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
        )
        {
            throw new InvalidOperationException(
                $"Refusing to use a test directory outside the system temp root: '{directory}'."
            );
        }
    }

    private static Process StartApplication(string dataDirectory)
    {
        var path = Environment.GetEnvironmentVariable("LIGHT_NOTES_PUBLISHED");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "LIGHT_NOTES_PUBLISHED must name the published Light Notes executable."
            );
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The published Light Notes executable does not exist.",
                fullPath
            );
        var start = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
        };
        start.Environment["LIGHT_NOTES_DATA_DIRECTORY"] = dataDirectory;
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not launch Light Notes.");
    }

    private static nint WaitForWindow(Process process)
    {
        nint handle = 0;
        WaitUntil(
            process,
            () =>
            {
                process.Refresh();
                handle = process.MainWindowHandle;
                return handle != 0;
            },
            "Light Notes did not expose a native window."
        );
        return handle;
    }

    private static AutomationElement FindByName(
        AutomationElement root,
        ControlType type,
        string name
    ) =>
        root.FindFirstDescendant(condition =>
            condition.ByControlType(type).And(condition.ByName(name))
        ) ?? throw new InvalidOperationException($"FlaUI could not find {type} named '{name}'.");

    private static bool TryReadValue(AutomationElement root, string name, out string value)
    {
        value = string.Empty;
        var field = root.FindFirstDescendant(condition =>
            condition.ByControlType(ControlType.Edit).And(condition.ByName(name))
        );
        if (field is null)
            return false;
        try
        {
            value = field.Patterns.Value.Pattern.Value.Value;
            return true;
        }
        catch (PatternNotSupportedException)
        {
            return false;
        }
    }

    private static bool TrySetValue(AutomationElement root, string name, string value)
    {
        var field = root.FindFirstDescendant(condition =>
            condition.ByControlType(ControlType.Edit).And(condition.ByName(name))
        );
        if (field is null)
            return false;
        try
        {
            field.Patterns.Value.Pattern.SetValue(value);
            return true;
        }
        catch (PatternNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryInvokeButton(AutomationElement root, string name)
    {
        var button = root.FindFirstDescendant(condition =>
            condition.ByControlType(ControlType.Button).And(condition.ByName(name))
        );
        if (button is null)
            return false;
        try
        {
            button.Patterns.Invoke.Pattern.Invoke();
            return true;
        }
        catch (PatternNotSupportedException)
        {
            return false;
        }
    }

    private static bool HasPersistedDraft(string directory, string title, string body)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "notes.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString()
        );
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM notes WHERE title = $title AND body = $body";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$body", body);
            return command.ExecuteScalar() is long count && count == 1;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
    }

    private static void SetFieldValue(
        Process process,
        AutomationElement root,
        string name,
        string value
    )
    {
        WaitUntil(
            process,
            () => TrySetValue(root, name, value),
            $"The {name} field rejected the requested value."
        );
        WaitUntil(
            process,
            () => TryReadValue(root, name, out var current) && current == value,
            $"The {name} field did not expose the requested value."
        );
    }

    private static void InvokeButton(Process process, AutomationElement root, string name)
    {
        WaitUntil(
            process,
            () => TryInvokeButton(root, name),
            $"The {name} button did not expose a usable Invoke pattern."
        );
    }

    private static void WaitForStatus(Process process, AutomationElement root, string expected)
    {
        WaitUntil(
            process,
            () =>
                root.FindAllDescendants(condition => condition.ByControlType(ControlType.StatusBar))
                    .Any(status => status.Name.Contains(expected, StringComparison.Ordinal)),
            $"Light Notes did not report status '{expected}'."
        );
    }

    private static void WaitForInitialStatus(Process process, AutomationElement root)
    {
        WaitUntil(
            process,
            () =>
                root.FindFirstDescendant(condition =>
                    condition
                        .ByControlType(ControlType.Edit)
                        .And(condition.ByName("Capture a link or thought"))
                )?.IsEnabled == true,
            "Light Notes did not finish opening the local store."
        );
    }

    private static AutomationElement WaitForElement(
        Process process,
        AutomationElement root,
        Func<AutomationElement?> find,
        string message
    )
    {
        AutomationElement? result = null;
        WaitUntil(process, () => (result = find()) is not null, message);
        return result!;
    }

    private static void WaitUntil(Process process, Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            process.Refresh();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Light Notes exited {process.ExitCode}: {message}"
                );
            if (elapsed.Elapsed >= Timeout)
                throw new TimeoutException(message);
            Thread.Sleep(50);
        }
    }

    private static void RecordDesktopFailure(Process process, string directory)
    {
        try
        {
            var output =
                Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "desktop");
            Directory.CreateDirectory(output);
            var prefix = Path.Combine(output, Path.GetFileName(directory));
            if (process.HasExited)
            {
                File.WriteAllText(prefix + ".txt", $"Process exited: {process.ExitCode}");
                return;
            }
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(process.MainWindowHandle);
            var lines = root.FindAllDescendants()
                .Select(element =>
                    $"{element.ControlType}: {element.Name}; enabled={element.IsEnabled}; bounds={element.BoundingRectangle}"
                );
            File.WriteAllLines(prefix + ".txt", lines);
            using var capture = Capture.Element(root);
            capture.ToFile(prefix + ".png");
        }
        catch (Exception error)
        {
            Console.WriteLine($"Desktop failure diagnostics unavailable: {error.GetType().Name}");
        }
    }

    private static void BringToForeground(Process process, nint handle)
    {
        // A desktop driver may not own foreground permission after another application was active.
        // Send and release Alt before requesting foreground, then verify the native window.
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
        _ = SetForegroundWindow(handle);
        WaitUntil(
            process,
            () => GetForegroundWindow() == handle,
            "Light Notes did not gain native foreground."
        );
    }

    private static void StopApplication(Process process)
    {
        if (process.HasExited)
            return;
        _ = process.CloseMainWindow();
        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}
