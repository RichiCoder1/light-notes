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
                AutomationElement? menu = null;
                WaitUntil(
                    process,
                    () => (menu = FindPublishedMenu(automation, process, handle)) is not null,
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
                    () => FindPublishedMenu(automation, process, handle) is null,
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

    [TestMethod]
    public void PublishedEditorContextMenuPreservesTextSelectionAndOwnerLifetime()
    {
        const string editedBody = "Context menu clipboard text";
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
                    () => TryReadValue(root, "Notes", out _),
                    "The seeded note editor did not load."
                );
                SetFieldValue(process, root, "Notes", editedBody);
                var editor = FindByName(root, ControlType.Edit, "Notes");
                editor.FocusNative();
                WaitUntil(
                    process,
                    () => editor.Properties.HasKeyboardFocus.Value,
                    "The note editor did not receive native focus."
                );
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                WaitUntil(
                    process,
                    () => SelectedText(editor) == editedBody,
                    "The note editor did not publish its selected text."
                );

                var target = editor.BoundingRectangle;
                Mouse.RightClick(new Point(target.Left + 24, target.Top + target.Height / 2));
                AutomationElement? menu = null;
                WaitUntil(
                    process,
                    () => (menu = FindPublishedMenu(automation, process, handle)) is not null,
                    "The editor context menu did not open."
                );
                foreach (
                    var command in new[] { "Undo", "Redo", "Cut", "Copy", "Paste", "Select all" }
                )
                    Assert.IsNotNull(
                        menu!.FindFirstDescendant(condition =>
                            condition
                                .ByControlType(ControlType.MenuItem)
                                .And(condition.ByName(command))
                        ),
                        "The editor menu omitted the standard " + command + " command."
                    );
                Assert.AreEqual(
                    editedBody,
                    SelectedText(editor),
                    "Right-click changed the existing editor selection."
                );
                var copy = menu!.FindFirstDescendant(condition =>
                    condition.ByControlType(ControlType.MenuItem).And(condition.ByName("Copy"))
                );
                Assert.IsNotNull(copy);
                Assert.IsTrue(copy.IsEnabled, "Copy was disabled for a nonempty selection.");
                copy.Patterns.Invoke.Pattern.Invoke();
                WaitUntil(
                    process,
                    () => FindPublishedMenu(automation, process, handle) is null,
                    "Copy did not dismiss the editor menu."
                );
                Assert.IsTrue(TryReadValue(root, "Notes", out var copiedBody));
                Assert.AreEqual(editedBody, copiedBody, "Copy changed the note text.");

                editor.FocusNative();
                // Supply the extended navigation scan code expected by SDL.
                Keyboard.TypeScanCode(0x4D, true);
                WaitUntil(
                    process,
                    () => SelectedText(editor) == string.Empty,
                    "The editor selection did not collapse before Select all."
                );
                Mouse.RightClick(new Point(target.Left + 24, target.Top + target.Height / 2));
                WaitUntil(
                    process,
                    () => (menu = FindPublishedMenu(automation, process, handle)) is not null,
                    "The editor menu did not reopen for Select all."
                );
                var selectAll = menu!.FindFirstDescendant(condition =>
                    condition
                        .ByControlType(ControlType.MenuItem)
                        .And(condition.ByName("Select all"))
                );
                Assert.IsNotNull(selectAll);
                Assert.IsTrue(selectAll.IsEnabled, "Select all was disabled for nonempty text.");
                selectAll.Patterns.Invoke.Pattern.Invoke();
                WaitUntil(
                    process,
                    () =>
                        FindPublishedMenu(automation, process, handle) is null
                        && SelectedText(editor) == editedBody,
                    "Select all did not run against the owner editor."
                );

                Mouse.RightClick(new Point(target.Left + 24, target.Top + target.Height / 2));
                WaitUntil(
                    process,
                    () => FindPublishedMenu(automation, process, handle) is not null,
                    "The editor menu did not reopen for Escape."
                );
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Keyboard.Release(VirtualKeyShort.ESCAPE);
                WaitUntil(
                    process,
                    () =>
                        FindPublishedMenu(automation, process, handle) is null
                        && editor.Properties.HasKeyboardFocus.Value,
                    "Escape did not dismiss the menu and restore editor focus."
                );
                Assert.AreEqual(
                    editedBody,
                    SelectedText(editor),
                    "Escape dismissal changed the editor selection."
                );

                Mouse.RightClick(new Point(target.Left + 24, target.Top + target.Height / 2));
                WaitUntil(
                    process,
                    () => FindPublishedMenu(automation, process, handle) is not null,
                    "The editor menu did not reopen for owner-close validation."
                );
                Assert.IsTrue(PostMessage(handle, 0x0010, 0, 0));
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

    private static string SelectedText(AutomationElement editor)
    {
        var selection = editor.Patterns.Text.Pattern.GetSelection();
        return selection.Length == 1 ? selection[0].GetText(-1) : string.Empty;
    }

    private static AutomationElement? FindPublishedMenu(
        UIA3Automation automation,
        System.Diagnostics.Process process,
        nint owner
    )
    {
        // Popup providers need not appear as process-filtered desktop children.
        // Match only native windows owned by the fixture process.
        foreach (var parent in new nint[] { 0, owner })
        {
            nint candidate = 0;
            while ((candidate = FindWindowEx(parent, candidate, null, null)) != 0)
            {
                _ = GetWindowThreadProcessId(candidate, out var candidateProcess);
                if (candidate == owner || candidateProcess != process.Id)
                    continue;
                var window = automation.FromHandle(candidate);
                var menu =
                    window.ControlType == ControlType.Menu
                        ? window
                        : window.FindFirstDescendant(condition =>
                            condition.ByControlType(ControlType.Menu)
                        );
                if (menu is not null)
                    return menu;
            }
        }
        return null;
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

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
