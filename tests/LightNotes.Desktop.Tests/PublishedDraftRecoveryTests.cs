using System.Diagnostics;
using System.IO;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    [TestMethod]
    public void PublishedInvalidDraftNavigatesReopensAndDiscardsWithoutChangingValidNote()
    {
        var directory = CreateTestDataDirectory();
        try
        {
            using (var process = StartApplication(directory))
            {
                try
                {
                    using var automation = new UIA3Automation();
                    var window = WaitForWindow(process);
                    var root = automation.FromHandle(window);
                    WaitForInitialStatus(process, root);
                    BringToForeground(process, window);
                    SetFieldValue(
                        process,
                        root,
                        "Capture a link or thought",
                        "Recovery desktop note"
                    );
                    InvokeButton(process, root, "Add");
                    WaitForStatus(process, root, "Saved on this device");
                    SetFieldValue(process, root, "Web address", "https://");
                    WaitUntil(
                        process,
                        () => RecoveryMatches(directory, present: true),
                        "Invalid autosave was not durably separated from the valid note."
                    );
                    WaitForStatus(process, root, "Draft saved on this device");
                    Assert.IsFalse(FindByName(root, ControlType.Button, "Open").IsEnabled);
                    SetFieldValue(process, root, "Search saved items", "Recovery");
                    SelectRow(process, root, "Archive");
                    SelectRow(process, root, "Inbox");
                    WaitUntil(
                        process,
                        () => TryReadValue(root, "Web address", out var url) && url == "https://",
                        "Returning to Inbox lost its selected note or invalid draft."
                    );
                    Assert.IsTrue(TryReadValue(root, "Search saved items", out var query));
                    Assert.AreEqual("Recovery", query);
                    Assert.IsTrue(process.CloseMainWindow());
                    WaitUntil(
                        process,
                        () => process.HasExited,
                        "A durable invalid draft blocked normal close."
                    );
                    Assert.AreEqual(0, process.ExitCode);
                }
                finally
                {
                    StopApplication(process);
                }
            }
            using (var process = StartApplication(directory))
            {
                try
                {
                    using var automation = new UIA3Automation();
                    var window = WaitForWindow(process);
                    var root = automation.FromHandle(window);
                    WaitForInitialStatus(process, root);
                    BringToForeground(process, window);
                    WaitUntil(
                        process,
                        () => TryReadValue(root, "Web address", out var url) && url == "https://",
                        "Reopening lost the recovered address draft."
                    );
                    WaitForStatus(process, root, "Draft saved on this device");
                    InvokeButton(process, root, "Discard draft");
                    WaitUntil(
                        process,
                        () => TryReadValue(root, "Web address", out var url) && url == "",
                        "Discard did not restore the last valid address."
                    );
                    WaitUntil(
                        process,
                        () => RecoveryMatches(directory, present: false),
                        "Discard did not remove the recovery record while preserving the saved note."
                    );
                    Assert.IsTrue(process.CloseMainWindow());
                    WaitUntil(
                        process,
                        () => process.HasExited,
                        "The discarded workspace did not close."
                    );
                    Assert.AreEqual(0, process.ExitCode);
                }
                finally
                {
                    StopApplication(process);
                }
            }
        }
        finally
        {
            DeleteTestDataDirectory(directory);
        }
    }

    private static bool RecoveryMatches(string directory, bool present)
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
            command.CommandText = present
                ? "SELECT COUNT(*) FROM notes n JOIN recovery_drafts d ON d.note_id = n.id WHERE n.title = 'Recovery desktop note' AND n.url IS NULL AND d.url = 'https://'"
                : "SELECT COUNT(*) FROM notes n WHERE n.title = 'Recovery desktop note' AND n.url IS NULL AND NOT EXISTS (SELECT 1 FROM recovery_drafts d WHERE d.note_id = n.id)";
            return command.ExecuteScalar() is long count && count == 1;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
    }
}
