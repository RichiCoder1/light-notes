using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LightNotes.ReviewFixtures;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    [TestMethod]
    public void PublishedNoteMenuPreservesSelectionAndDismisses()
    {
        var directory = CreateTestDataDirectory();
        try
        {
            ReviewSamples.SeedAsync(Path.Combine(directory, "notes.db")).GetAwaiter().GetResult();
            using var process = StartApplication(directory);
            try
            {
                using var automation = new UIA3Automation();
                var handle = WaitForWindow(process);
                var root = automation.FromHandle(handle);
                WaitForInitialStatus(process, root);
                BringToForeground(process, handle);
                SelectRow(process, root, "Questions for");
                WaitUntil(
                    process,
                    () =>
                        TryReadValue(root, "Title", out var title)
                        && title.StartsWith("Questions for", StringComparison.Ordinal),
                    "The active note did not load."
                );
                var target = root.FindAllDescendants(c => c.ByControlType(ControlType.ListItem))
                    .Single(row =>
                        row.Name.StartsWith("SQLite is transactional", StringComparison.Ordinal)
                    );
                var output =
                    Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                    ?? Path.Combine("artifacts", "desktop");
                Directory.CreateDirectory(output);
                CaptureShell(root, output, "note-menu-before.png");
                BringToForeground(process, handle);
                var bounds = target.BoundingRectangle;
                Mouse.RightClick(new Point(bounds.Left + 12, bounds.Top + bounds.Height / 2));
                AutomationElement? FindMenu()
                {
                    // Popup providers need not appear as process-filtered desktop children.
                    // Match the framework's published fixture through native window identity.
                    foreach (var parent in new nint[] { 0, handle })
                    {
                        nint candidate = 0;
                        while ((candidate = FindWindowEx(parent, candidate, null, null)) != 0)
                        {
                            _ = GetWindowThreadProcessId(candidate, out var candidateProcess);
                            if (candidate == handle || candidateProcess != process.Id)
                                continue;
                            var window = automation.FromHandle(candidate);
                            var result =
                                window.ControlType == ControlType.Menu
                                    ? window
                                    : window.FindFirstDescendant(c =>
                                        c.ByControlType(ControlType.Menu)
                                    );
                            if (result is not null)
                                return result;
                        }
                    }
                    return null;
                }
                AutomationElement? menu = null;
                WaitUntil(
                    process,
                    () => (menu = FindMenu()) is not null,
                    "The note context menu did not open."
                );
                Assert.IsNotNull(
                    menu!.FindFirstDescendant(c =>
                        c.ByControlType(ControlType.MenuItem).And(c.ByName("Archive"))
                    )
                );
                Assert.IsFalse(
                    target.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    "Opening the target menu changed note selection."
                );
                Assert.IsTrue(TryReadValue(root, "Title", out var activeTitle));
                Assert.IsTrue(activeTitle.StartsWith("Questions for", StringComparison.Ordinal));
                CaptureShell(root, output, "note-menu-open.png");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Keyboard.Release(VirtualKeyShort.ESCAPE);
                WaitUntil(
                    process,
                    () => FindMenu() is null,
                    "Escape did not dismiss the note menu."
                );
                Assert.IsFalse(target.Patterns.SelectionItem.Pattern.IsSelected.Value);
                CaptureShell(root, output, "note-menu-dismissed.png");
                Assert.IsTrue(process.CloseMainWindow());
                Assert.IsTrue(process.WaitForExit(30_000));
                Assert.AreEqual(0, process.ExitCode);
            }
            catch
            {
                RecordDesktopFailure(process, directory);
                throw;
            }
            finally
            {
                StopApplication(process);
            }
        }
        finally
        {
            DeleteTestDataDirectory(directory);
        }
    }

    [LibraryImport(
        "user32.dll",
        EntryPoint = "FindWindowExW",
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial nint FindWindowEx(
        nint parent,
        nint childAfter,
        string? className,
        string? windowName
    );
}
