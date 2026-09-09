using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    private static readonly nint PerMonitorV2DpiAwareness = (nint)(-4);
    private nint _priorDpiAwareness;

    [TestInitialize]
    public void UsePhysicalDesktopCoordinates()
    {
        _priorDpiAwareness = SetThreadDpiAwarenessContext(PerMonitorV2DpiAwareness);
        Assert.AreNotEqual(
            nint.Zero,
            _priorDpiAwareness,
            "The desktop test thread could not enter per-monitor-v2 DPI awareness."
        );
    }

    [TestCleanup]
    public void RestoreDesktopCoordinateContext()
    {
        if (_priorDpiAwareness != nint.Zero)
            _ = SetThreadDpiAwarenessContext(_priorDpiAwareness);
    }

    [TestMethod]
    public void PublishedEditorChangesNativeCursorAndBlinksWithoutInput()
    {
        var directory = CreateTestDataDirectory();
        try
        {
            using var process = StartApplication(directory);
            try
            {
                using var automation = new UIA3Automation();
                var handle = WaitForWindow(process);
                var root = automation.FromHandle(handle);
                WaitForInitialStatus(process, root);
                BringToForeground(process, handle);
                SetFieldValue(process, root, "Capture a link or thought", "Caret review note");
                InvokeButton(process, root, "Add");
                SetFieldValue(process, root, "Notes", "A blinking caret.");
                var editor = FindByName(root, ControlType.Edit, "Notes");
                editor.Focus();
                WaitUntil(
                    process,
                    () => editor.Properties.HasKeyboardFocus.Value,
                    "The editor did not gain caret focus."
                );
                WaitForStatus(process, root, "Saved on this device");
                var add = FindByName(root, ControlType.Button, "Add").BoundingRectangle;
                Mouse.MoveTo(new Point(add.Left + add.Width / 2, add.Top + add.Height / 2));
                Wait.UntilInputIsProcessed();
                Thread.Sleep(150);
                var arrow = CursorHandle();
                var bounds = editor.BoundingRectangle;
                Mouse.MoveTo(
                    new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
                );
                WaitUntil(
                    process,
                    () => CursorHandle() != arrow,
                    "Moving into the editor did not change the native pointer cursor."
                );
                Mouse.MoveTo(new Point(add.Left + add.Width / 2, add.Top + add.Height / 2));
                WaitUntil(
                    process,
                    () => CursorHandle() == arrow,
                    "Leaving the editor did not restore the default cursor."
                );

                // UIA focus and native foreground are separate. Establish both after pointer checks.
                BringToForeground(process, handle);
                editor.Focus();
                WaitUntil(
                    process,
                    () => editor.Properties.HasKeyboardFocus.Value,
                    "The editor did not become the foreground caret owner."
                );
                var scale = GetDpiForWindow(handle) / 96d;
                var rectangle = new Rectangle(
                    bounds.Left + (int)(3 * scale),
                    bounds.Top + (int)(3 * scale),
                    Math.Min(bounds.Width - (int)(6 * scale), (int)(250 * scale)),
                    (int)(45 * scale)
                );
                var output =
                    Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                    ?? Path.Combine("artifacts", "desktop");
                Directory.CreateDirectory(output);
                var interval = GetCaretBlinkTime();
                var duration =
                    interval == uint.MaxValue
                        ? 650
                        : Math.Clamp(
                            (long)(interval == 0 ? 500 : interval) * 2 + 300,
                            1300,
                            10_000
                        );
                var signatures = new HashSet<ulong>();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    var foreground = GetForegroundWindow();
                    _ = GetWindowThreadProcessId(foreground, out var foregroundProcessId);
                    Assert.AreEqual(
                        handle,
                        foreground,
                        $"Foreground changed while observing caret frames (foreground PID {foregroundProcessId}; app exited: {process.HasExited})."
                    );
                    using var capture = Capture.Rectangle(rectangle);
                    ulong signature = 14695981039346656037;
                    for (var y = 0; y < capture.Bitmap.Height; y++)
                        for (var x = 0; x < capture.Bitmap.Width; x++)
                            signature = unchecked(
                                (signature ^ (uint)capture.Bitmap.GetPixel(x, y).ToArgb())
                                * 1099511628211
                            );
                    if (signatures.Add(signature))
                        capture.ToFile(Path.Combine(output, $"caret-phase-{signatures.Count}.png"));
                    Thread.Sleep(100);
                } while (timer.ElapsedMilliseconds < duration && signatures.Count < 2);
                if (interval == uint.MaxValue)
                    Assert.AreEqual(
                        1,
                        signatures.Count,
                        "Windows disabled blinking but the editor pixels changed."
                    );
                else
                    Assert.AreEqual(
                        2,
                        signatures.Count,
                        "The focused editor never painted both caret phases while idle."
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

    private static nint CursorHandle()
    {
        var cursor = new NativeCursor { Size = (uint)Marshal.SizeOf<NativeCursor>() };
        Assert.IsTrue(GetCursorInfo(ref cursor));
        return cursor.Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursor
    {
        public uint Size;
        public uint Flags;
        public nint Handle;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref NativeCursor cursor);

    [LibraryImport("user32.dll")]
    private static partial uint GetCaretBlinkTime();

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
