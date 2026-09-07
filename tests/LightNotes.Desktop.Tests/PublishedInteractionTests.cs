using System.Diagnostics;
using System.Drawing;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using LightNotes.ReviewFixtures;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    [TestMethod]
    public void PublishedLongNoteScrollAndArchiveRoundTripRemainsInteractive()
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
                SelectRow(process, root, "Long note");
                WaitUntil(
                    process,
                    () => TryReadValue(root, "Notes", out var body) && body.Length == 20_000,
                    "Long note selection did not finish loading the full editor."
                );
                var editor = FindByName(root, ControlType.Edit, "Notes");
                var bounds = editor.BoundingRectangle;
                Mouse.LeftClick(
                    new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
                );
                var scroll = editor.Patterns.Scroll.Pattern;
                scroll.SetScrollPercent(-1, 0);
                WaitUntil(
                    process,
                    () => scroll.VerticalScrollPercent.Value == 0,
                    "Long note could not scroll back to its start."
                );
                Mouse.MoveTo(
                    new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
                );
                Mouse.Scroll(-2);
                WaitUntil(
                    process,
                    () => scroll.VerticalScrollPercent.Value > 0,
                    "Physical wheel input did not scroll the long note."
                );
                var output =
                    Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                    ?? Path.Combine("artifacts", "desktop");
                Directory.CreateDirectory(output);
                CaptureShell(root, output, "long-note-wheel.png");
                scroll.SetScrollPercent(-1, 0);
                WaitUntil(
                    process,
                    () => scroll.VerticalScrollPercent.Value == 0,
                    "Long note did not reset before thumb dragging."
                );
                var scale = GetDpiForWindow(handle) / 96d;
                var trackHeight = bounds.Height - 28 * scale;
                var thumbHeight = Math.Max(
                    28 * scale,
                    trackHeight * scroll.VerticalViewSize.Value / 100
                );
                var thumbCenter = new Point(
                    bounds.Right - (int)(19 * scale),
                    bounds.Top + (int)(14 * scale + thumbHeight / 2)
                );
                Mouse.MoveTo(thumbCenter);
                Mouse.Down(MouseButton.Left);
                try
                {
                    Mouse.MoveTo(new Point(thumbCenter.X, thumbCenter.Y + (int)(60 * scale)));
                    WaitUntil(
                        process,
                        () => scroll.VerticalScrollPercent.Value > 0,
                        "Dragging the visible thumb did not change the editor's UIA scroll position."
                    );
                }
                finally
                {
                    Mouse.Up(MouseButton.Left);
                }
                CaptureShell(root, output, "long-note-thumb-drag.png");
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    InvokeButton(process, root, "Archive");
                    WaitForLongNoteToLeaveCollection(process, root);
                    WaitForStatus(process, root, "Saved on this device");
                    SelectRow(process, root, "Archive");
                    SelectRow(process, root, "Long note");
                    WaitUntil(
                        process,
                        () => TryReadValue(root, "Notes", out var body) && body.Length == 20_000,
                        "Archive navigation lost the long note or stopped responding."
                    );
                    InvokeButton(process, root, "Restore");
                    WaitForLongNoteToLeaveCollection(process, root);
                    WaitForStatus(process, root, "Saved on this device");
                    SelectRow(process, root, "Inbox");
                    SelectRow(process, root, "Long note");
                }
                WaitUntil(
                    process,
                    () =>
                        TryReadValue(root, "Notes", out var restoredBody)
                        && restoredBody.Length == 20_000,
                    "The final restored long note did not finish loading."
                );
                CaptureShell(root, output, "long-note-archive-roundtrip.png");
                Assert.IsFalse(
                    File.Exists(Path.Combine(directory, "last-crash.txt")),
                    "The native interaction path produced a crash report."
                );
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

    private static void WaitForLongNoteToLeaveCollection(Process process, AutomationElement root) =>
        WaitUntil(
            process,
            () =>
                !root.FindAllDescendants(c => c.ByControlType(ControlType.ListItem))
                    .Any(item => item.Name.StartsWith("Long note", StringComparison.Ordinal)),
            "Archive/restore did not finish updating the current collection."
        );

    private static void SelectRow(Process process, AutomationElement root, string prefix)
    {
        var row = WaitForElement(
            process,
            root,
            () =>
                root.FindAllDescendants(c => c.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item =>
                        item.IsEnabled && item.Name.StartsWith(prefix, StringComparison.Ordinal)
                    ),
            "Could not find list item beginning with " + prefix
        );
        row.Patterns.SelectionItem.Pattern.Select();
    }
}
