using System.IO;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LightNotes.ReviewFixtures;

namespace LightNotes.Desktop.Tests;

public sealed partial class PublishedPersistenceTests
{
    [TestMethod]
    public void PublishedResponsiveCollapseRehomesFocusWithoutReplayingIt()
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
                ResizeClient(process, handle, 1180, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && HasField(root, "Title"),
                    "Wide shell did not expose both panes."
                );
                var list =
                    root.FindFirstDescendant(c =>
                        c.ByControlType(ControlType.List).And(c.ByName("Saved notes"))
                    ) ?? throw new InvalidOperationException("The saved notes list is absent.");
                var row =
                    list.FindAllDescendants(c => c.ByControlType(ControlType.ListItem))
                        .FirstOrDefault(item =>
                            !item.Patterns.SelectionItem.Pattern.IsSelected.Value
                        )
                    ?? throw new InvalidOperationException("The seeded collection has no row.");
                row.Patterns.SelectionItem.Pattern.Select();
                WaitUntil(
                    process,
                    () => row.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    "Selecting an unselected note was not applied."
                );
                WaitUntil(
                    process,
                    () =>
                        FindByName(
                            root,
                            ControlType.Edit,
                            "Title"
                        ).Properties.HasKeyboardFocus.Value,
                    "Opening a different note did not establish editor focus."
                );
                var search = FindByName(root, ControlType.Edit, "Search saved items");
                search.Focus();
                WaitUntil(
                    process,
                    () => search.Properties.HasKeyboardFocus.Value,
                    "Search did not take focus before collapse."
                );
                ResizeClient(process, handle, 560, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Title") && !HasField(root, "Search saved items"),
                    "Compact editor did not hide the focused search."
                );
                WaitUntil(
                    process,
                    () =>
                        root.FindAllDescendants()
                            .Any(element => element.Properties.HasKeyboardFocus.ValueOrDefault),
                    "Collapsing the focused collection cleared keyboard focus without recovery."
                );
                var title = FindByName(root, ControlType.Edit, "Title");
                title.Focus();
                WaitUntil(
                    process,
                    () => title.Properties.HasKeyboardFocus.Value,
                    "The visible title did not accept explicit focus."
                );
                ResizeClient(process, handle, 1180, 640);
                WaitUntil(
                    process,
                    () => HasField(root, "Search saved items") && HasField(root, "Title"),
                    "Returning wide did not restore the collection."
                );
                Assert.IsTrue(
                    FindByName(root, ControlType.Edit, "Title").Properties.HasKeyboardFocus.Value,
                    "Revealing the collection must not replay its old search focus."
                );
                Assert.IsTrue(process.CloseMainWindow());
                Assert.IsTrue(process.WaitForExit(30_000));
                Assert.AreEqual(0, process.ExitCode);
            }
            catch
            {
                RecordDesktopFailure(process, dataDirectory);
                throw;
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
}
