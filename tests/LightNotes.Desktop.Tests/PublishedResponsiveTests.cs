using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LightNotes.ReviewFixtures;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    [TestMethod]
    public void PublishedShellResizesAndRestoresDraftWithAccessibleFocus()
    {
        var dataDirectory = CreateTestDataDirectory();
        try
        {
            ReviewSamples
                .SeedAsync(Path.Combine(dataDirectory, "notes.db"))
                .GetAwaiter()
                .GetResult();
            using var process = StartApplication(dataDirectory);
            try
            {
                var handle = WaitForWindow(process);
                using var automation = new UIA3Automation();
                var root = automation.FromHandle(handle);
                WaitForInitialStatus(process, root);
                BringToForeground(process, handle);
                var output = Path.GetFullPath(
                    Environment.GetEnvironmentVariable("LIGHT_NOTES_DESKTOP_ARTIFACTS")
                        ?? Path.Combine("artifacts", "desktop")
                );
                Directory.CreateDirectory(output);
                ResizeClient(process, handle, 1180, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && HasField(root, "Notes"),
                    "Wide shell omitted a pane."
                );
                SetFieldValue(
                    process,
                    root,
                    "Notes",
                    "A draft kept across window sizes.\nSecond line."
                );
                CaptureShell(root, output, "shell-wide.png");
                ScanShell(process, handle, output, "wide");
                ResizeClient(process, handle, 900, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && HasField(root, "Notes"),
                    "Medium shell omitted a pane."
                );
                CaptureShell(root, output, "shell-medium.png");
                ResizeClient(process, handle, 560, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && !HasField(root, "Notes"),
                    "Compact collection exposed its hidden editor."
                );
                var row =
                    root.FindFirstDescendant(c => c.ByControlType(ControlType.ListItem))
                    ?? throw new InvalidOperationException("Compact collection omitted rows.");
                row.Patterns.SelectionItem.Pattern.Select();
                WaitUntil(
                    process,
                    () => HasField(root, "Notes") && !HasField(root, "Search saved items"),
                    "Compact editor did not replace the collection."
                );
                Assert.IsTrue(TryReadValue(root, "Notes", out var body));
                Assert.AreEqual("A draft kept across window sizes.\nSecond line.", body);
                CaptureShell(root, output, "shell-compact-editor.png");
                ScanShell(process, handle, output, "compact-editor");
                InvokeButton(process, root, "Back to collection");
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && !HasField(root, "Notes"),
                    "Back did not restore the collection."
                );
                WaitUntil(
                    process,
                    () =>
                        FindByName(
                            root,
                            ControlType.Edit,
                            "Search saved items"
                        ).Properties.HasKeyboardFocus.Value,
                    "Back did not restore useful keyboard focus."
                );
                CaptureShell(root, output, "shell-compact-collection.png");
                ResizeClient(process, handle, 300, 300, expectMinimum: true);
                CaptureShell(root, output, "shell-minimum.png");
                ScanShell(process, handle, output, "minimum-collection");
                ResizeClient(process, handle, 1180, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Notes") && HasField(root, "Search saved items"),
                    "Returning wide did not restore both panes."
                );
                Assert.IsTrue(TryReadValue(root, "Notes", out body));
                Assert.AreEqual("A draft kept across window sizes.\nSecond line.", body);
                root.SetForeground();
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);
                WaitUntil(
                    process,
                    () =>
                        FindByName(
                            root,
                            ControlType.Edit,
                            "Capture a link or thought"
                        ).Properties.HasKeyboardFocus.Value,
                    "Ctrl+N did not focus capture."
                );
                // Autosave can finish during resizing and accessibility scans.
                WaitForStatus(process, root, "Saved on this device");
                Assert.IsFalse(
                    FindByName(root, ControlType.Button, "Save now").IsEnabled,
                    "Save now should be disabled after autosave has committed the current draft."
                );
                Assert.IsTrue(process.CloseMainWindow());
                Assert.IsTrue(process.WaitForExit(30_000));
                Assert.AreEqual(0, process.ExitCode);
            }
            finally
            {
                StopApplication(process);
            }
        }
        finally
        {
            DeleteTestDataDirectory(dataDirectory);
        }
    }

    private static void CaptureShell(AutomationElement root, string output, string name)
    {
        // UIA/layout readiness can precede the next presented frame. This delay is
        // only for screenshot evidence; behavioral assertions keep their own waits.
        Wait.UntilInputIsProcessed();
        Thread.Sleep(200);
        Capture.Element(root).ToFile(Path.Combine(output, name));
    }

    private static bool HasField(AutomationElement root, string name) =>
        root.FindFirstDescendant(c => c.ByControlType(ControlType.Edit).And(c.ByName(name)))
            is not null;

    private static void ScanShell(Process process, nint handle, string output, string name)
    {
        var directory = Path.Combine(output, "axe-" + name);
        Directory.CreateDirectory(directory);
        var config = Config
            .Builder.ForProcessId(process.Id)
            .WithOutputFileFormat(OutputFileFormat.A11yTest)
            .WithOutputDirectory(directory)
            .WithAlwaysSaveTestFile()
            .Build();
        var scan = ScannerFactory.CreateScanner(config).Scan(new ScanOptions(name, handle));
        Assert.IsTrue(scan.WindowScanOutputs.Count > 0, "Axe returned no windows.");
        Assert.AreEqual(
            0,
            scan.WindowScanOutputs.Sum(result => result.ErrorCount),
            string.Join(
                "; ",
                scan.WindowScanOutputs.SelectMany(result => result.Errors)
                    .Select(error => error.Rule.ID + ": " + error.Rule.Description)
            )
        );
    }

    private static void ResizeClient(
        Process process,
        nint handle,
        int width,
        int height,
        bool expectMinimum = false
    )
    {
        Assert.IsTrue(GetClientRect(handle, out var client));
        var scale = GetDpiForWindow(handle) / 96d;
        Assert.IsTrue(GetWindowRect(handle, out var outer));
        var frameWidth = outer.Right - outer.Left - client.Right;
        var frameHeight = outer.Bottom - outer.Top - client.Bottom;
        if (expectMinimum)
        {
            var limits = new WindowLimits();
            _ = SendMessage(handle, 0x24, 0, ref limits); // WM_GETMINMAXINFO: OS tracking limits.
            Assert.IsTrue(
                Math.Abs((limits.MinTrack.X - frameWidth) / scale - 480) < 2,
                "Published minimum client width was not registered with Windows."
            );
            Assert.IsTrue(
                Math.Abs((limits.MinTrack.Y - frameHeight) / scale - 520) < 2,
                "Published minimum client height was not registered with Windows."
            );
            width = 480;
            height = 520;
        }
        Assert.IsTrue(
            SetWindowPos(
                handle,
                0,
                0,
                0,
                (int)Math.Ceiling(width * scale + frameWidth),
                (int)Math.Ceiling(height * scale + frameHeight),
                0x16
            )
        );
        WaitUntil(
            process,
            () =>
            {
                if (!GetClientRect(handle, out var current))
                    return false;
                return Math.Abs(current.Right / scale - (expectMinimum ? 480 : width)) < 2
                    && Math.Abs(current.Bottom / scale - (expectMinimum ? 520 : height)) < 2;
            },
            "The client window did not honor the logical size/minimum."
        );
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out ClientRectangle rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window,
        nint after,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(
        nint window,
        uint message,
        nint wparam,
        ref WindowLimits limits
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowLimits
    {
        public WindowPoint Reserved;
        public WindowPoint MaxSize;
        public WindowPoint MaxPosition;
        public WindowPoint MinTrack;
        public WindowPoint MaxTrack;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out ClientRectangle rectangle);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
