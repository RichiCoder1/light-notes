using System.Collections.Concurrent;
using LightNotes.Storage;
using Lucent.Core;

namespace LightNotes.Tests;

[TestClass]
public sealed class WorkspaceTests
{
    [TestMethod]
    public void CaptureSaveArchiveAndClosePersistAcrossWorkspaceRestart()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.IsTrue(model.IsReady);
        model.Capture.Text = "A thought to keep";
        fixture.Execute(model.CaptureCommand);
        var id = model.Selected.Value!.Id;
        Assert.AreEqual(1, model.Items.Value.Count);
        model.Title.Text = "A revised thought";
        model.Body.Text = "This is the latest accepted draft.\nA second line with a tab:\tkept.";
        Assert.IsTrue(model.IsDirty);
        fixture.Execute(model.SaveCommand);
        Assert.IsFalse(model.IsDirty);
        fixture.Execute(model.ArchiveCommand);
        Assert.AreEqual(0, model.Items.Value.Count);
        fixture.Execute(model.ToggleArchiveCommand);
        Assert.AreEqual(id, model.Items.Value.Single().Id);
        fixture.Pump(model.PrepareCloseAsync().AsTask());
        fixture.Pump(model.DisposeAsync().AsTask());
        var storeTask = NoteStore.OpenAsync(fixture.DatabasePath);
        fixture.Pump(storeTask);
        var store = storeTask.Result;
        var records = store.ListAsync(includeArchived: true);
        fixture.Pump(records);
        Assert.AreEqual("A revised thought", records.Result.Single().Title);
        Assert.AreEqual(
            "This is the latest accepted draft.\nA second line with a tab:\tkept.",
            records.Result.Single().Body
        );
        Assert.IsTrue(records.Result.Single().IsArchived);
        fixture.Pump(store.DisposeAsync().AsTask());
    }

    [TestMethod]
    public void CloseSavesDirtyDraftAndFailureKeepsItAvailableForRetry()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "Save on close";
        fixture.Execute(model.CaptureCommand);
        model.Title.Text = "";
        var rejected = model.PrepareCloseAsync().AsTask();
        fixture.Pump(rejected);
        Assert.IsFalse(rejected.Result);
        Assert.IsTrue(model.IsDirty);
        Assert.IsTrue(model.RetryCommand.IsEnabled);
        model.Title.Text = "Corrected draft";
        fixture.Execute(model.RetryCommand);
        Assert.IsFalse(model.IsDirty);
        model.Body.Text = "Close accepts this final change.";
        var accepted = model.PrepareCloseAsync().AsTask();
        fixture.Pump(accepted);
        Assert.IsTrue(accepted.Result);
        Assert.IsFalse(model.IsDirty);
    }

    [TestMethod]
    public void SwitchingRecordsSavesThePreviousDraftWithoutLosingSessionIdentity()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "First";
        fixture.Execute(model.CaptureCommand);
        var first = model.Selected.Value!.Id;
        model.Capture.Text = "https://example.com/notes";
        fixture.Execute(model.CaptureCommand);
        var second = model.Selected.Value!.Id;
        model.Title.Text = "Second, edited";
        model.Select(first);
        fixture.Until(() => !model.IsBusy);
        Assert.AreEqual(first, model.Selected.Value!.Id);
        Assert.AreEqual(first.ToString(), model.Title.DocumentId);
        model.Select(second);
        fixture.Until(() => !model.IsBusy);
        Assert.AreEqual("Second, edited", model.Title.Text);
        Assert.AreEqual(NoteKind.Link, model.Selected.Value!.Kind);
    }

    [TestMethod]
    public void SearchFiltersLoadedRecordsWithoutDiscardingTheSelectedDraft()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "First searchable note";
        fixture.Execute(model.CaptureCommand);
        var first = model.Selected.Value!.Id;
        model.Capture.Text = "Second searchable note";
        fixture.Execute(model.CaptureCommand);

        model.Select(first);
        fixture.Until(() => !model.IsBusy);
        model.Body.Text = "Draft survives a filtered collection.";
        model.Search.Text = "first";
        fixture.Until(() => model.VisibleItems.Count == 1);
        Assert.AreEqual(first, model.VisibleItems.Single().Id);

        model.Search.Text = "no matching note";
        fixture.Until(() => model.VisibleItems.Count == 0);
        Assert.AreEqual(first, model.Selected.Value!.Id);
        Assert.AreEqual("Draft survives a filtered collection.", model.Body.Text);
        StringAssert.Contains(model.EmptyStateText, "no matching note");

        model.Search.Text = string.Empty;
        fixture.Until(() => model.VisibleItems.Count == 2);
        Assert.AreEqual("Draft survives a filtered collection.", model.Body.Text);
    }

    [TestMethod]
    public void CompactBackPreservesSearchSelectionAndDraft()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "A compact route note";
        fixture.Execute(model.CaptureCommand);
        var id = model.Selected.Value!.Id;
        model.Body.Text = "Keep this draft while switching routes.";
        model.Search.Text = "compact";
        fixture.Until(() => model.VisibleItems.Count == 1);

        Assert.IsTrue(model.IsCompact);
        Assert.IsTrue(model.ShowEditor);
        model.BackToCollection();

        Assert.AreEqual(NoteWorkspaceRoute.Collection, model.Route.Value);
        Assert.IsTrue(model.ShowCollection);
        Assert.IsFalse(model.ShowEditor);
        Assert.AreEqual(id, model.Selected.Value!.Id);
        Assert.AreEqual("compact", model.Query);
        Assert.AreEqual("Keep this draft while switching routes.", model.Body.Text);

        model.Select(id);
        Assert.AreEqual(NoteWorkspaceRoute.Editor, model.Route.Value);
        Assert.AreEqual("Keep this draft while switching routes.", model.Body.Text);
    }

    [TestMethod]
    public void ExplicitCollectionDestinationsDoNotToggleToTheWrongCollection()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "Move between collections";
        fixture.Execute(model.CaptureCommand);
        fixture.Execute(model.ArchiveCommand);
        Assert.IsFalse(model.ShowArchived.Value);

        model.ShowArchive();
        fixture.Until(() => !model.IsBusy);
        Assert.IsTrue(model.ShowArchived.Value);
        Assert.AreEqual(1, model.ArchiveCount);

        model.ShowInbox();
        fixture.Until(() => !model.IsBusy);
        Assert.IsFalse(model.ShowArchived.Value);
        Assert.AreEqual(0, model.InboxCount);
    }

    [TestMethod]
    public void AConcurrentDiskChangePreservesTheDraftAndAllowsExplicitRetry()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "Original";
        fixture.Execute(model.CaptureCommand);
        var original = model.Selected.Value!;
        var open = NoteStore.OpenAsync(fixture.DatabasePath);
        fixture.Pump(open);
        var secondStore = open.Result;
        fixture.Pump(
            secondStore.SaveAsync(
                new(
                    original.Id,
                    NoteKind.Note,
                    "Other window",
                    null,
                    "Other body",
                    original.Revision
                )
            )
        );
        fixture.Pump(secondStore.DisposeAsync().AsTask());
        model.Title.Text = "My draft";
        fixture.Execute(model.SaveCommand);
        Assert.IsTrue(model.IsDirty);
        Assert.AreEqual("My draft", model.Title.Text);
        StringAssert.Contains(model.StatusText, "changed on disk");
        fixture.Execute(model.RetryCommand);
        Assert.IsFalse(model.IsDirty);
        Assert.AreEqual("My draft", model.Selected.Value!.Title);
        var close = model.PrepareCloseAsync().AsTask();
        fixture.Pump(close);
        Assert.IsTrue(close.Result);
    }

    private sealed class Fixture : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _prior = Current;
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly ReactiveGraph _graph = new();
        private readonly ReactiveScope _scope;
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "light-notes-model-" + Guid.NewGuid().ToString("N")
        );

        public Fixture()
        {
            SetSynchronizationContext(this);
            _scope = _graph.CreateScope("workspace-test");
            Model = new(_scope, DatabasePath);
        }

        public string DatabasePath => Path.Combine(_directory, "notes.db");
        public NoteWorkspace Model { get; }

        public override void Post(SendOrPostCallback callback, object? state) =>
            _queue.Enqueue(() => callback(state));

        public void Pump(Task task)
        {
            Until(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        public void Execute(ApplicationCommand command)
        {
            Assert.IsTrue(command.TryExecute());
            Until(() => !command.IsBusy && !Model.IsBusy);
            Assert.IsNull(command.Error);
        }

        public void Until(Func<bool> complete)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!complete())
            {
                while (_queue.TryDequeue(out var callback))
                    callback();
                _graph.Drain();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Workspace work did not complete.");
                Thread.Sleep(1);
            }
            _graph.Drain();
        }

        public void Dispose()
        {
            try
            {
                Pump(Model.DisposeAsync().AsTask());
                _scope.Dispose();
            }
            finally
            {
                SetSynchronizationContext(_prior);
            }
            if (Directory.Exists(_directory))
            {
                var full = Path.GetFullPath(_directory);
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
                if (
                    !full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(full)
                        .StartsWith("light-notes-model-", StringComparison.Ordinal)
                )
                    throw new InvalidOperationException(
                        "Refusing cleanup outside the test data directory."
                    );
                Directory.Delete(full, true);
            }
        }
    }
}
