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
    public void CloseAcceptsDurableInvalidDraftWithoutReportingStorageFailure()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "Save on close";
        fixture.Execute(model.CaptureCommand);
        model.Title.Text = "";
        var rejected = model.PrepareCloseAsync().AsTask();
        fixture.Pump(rejected);
        Assert.IsTrue(rejected.Result);
        Assert.IsTrue(model.IsDirty);
        Assert.IsFalse(model.HasError);
    }

    [TestMethod]
    public void CloseAfterStartupFailureDoesNotRequireAnInitializedDraftWriter()
    {
        using var fixture = new Fixture(new IOException("database unavailable"));
        fixture.Pump(fixture.Model.StartAsync());
        Assert.IsTrue(fixture.Model.HasError);

        var close = fixture.Model.PrepareCloseAsync().AsTask();
        fixture.Pump(close);
        Assert.IsTrue(close.Result);
    }

    [TestMethod]
    public void CancelledCloseStopsWaitingButAnAcceptedSaveStillCompletes()
    {
        var storage = new ControlledStorage(ControlledStorage.Record("Initial", "Before"));
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Body.Text = "Accepted before close";
        Assert.IsTrue(model.SaveCommand.TryExecute());
        fixture.Until(() => storage.Saves.Count == 1);

        using var cancellation = new CancellationTokenSource();
        var close = model.PrepareCloseAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            fixture.Pump(close);
            Assert.Fail("Cancelled close preparation completed successfully.");
        }
        catch (OperationCanceledException) { }

        Assert.IsFalse(model.HasError);
        storage.CompleteSave(0);
        fixture.Until(() => !model.IsBusy);
        Assert.AreEqual("Accepted before close", storage.Current.Body);
        Assert.IsFalse(model.HasError);
    }

    [TestMethod]
    public void CancelledStorageFencePropagatesWithoutBecomingASaveError()
    {
        var storage = new CancellableCloseStorage();
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        using var cancellation = new CancellationTokenSource();
        var close = model.PrepareCloseAsync(cancellation.Token).AsTask();
        fixture.Until(() => storage.CloseStarted);

        cancellation.Cancel();
        try
        {
            fixture.Pump(close);
            Assert.Fail("Cancelled close preparation completed successfully.");
        }
        catch (OperationCanceledException) { }

        Assert.AreEqual(cancellation.Token, storage.CloseToken);
        Assert.IsFalse(model.HasError);
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
    public void SearchAcceptsLatestQueryWhileArchiveReloadIsPending()
    {
        var inbox = ControlledStorage.Record("Inbox", "Current note");
        var matchingArchive = ControlledStorage.Record("Matching archive", "Find this") with
        {
            IsArchived = true,
        };
        var otherArchive = ControlledStorage.Record("Other archive", "Hide this") with
        {
            IsArchived = true,
        };
        var storage = new DelayedReloadStorage([inbox], [inbox, matchingArchive, otherArchive]);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;

        Assert.IsFalse(model.CanSearch);
        fixture.Pump(model.StartAsync());
        Assert.IsTrue(model.CanSearch);

        model.ShowArchive();
        fixture.Until(() => model.ShowArchived.Value && storage.ReloadPending);
        Assert.IsTrue(model.CanSearch, "An ordinary collection reload disabled search input.");
        model.Search.Text = "matching";
        fixture.Drain();

        storage.CompleteReload();
        fixture.Until(() => model.VisibleItems.Count == 1);
        Assert.AreEqual("matching", model.Query);
        Assert.AreEqual(matchingArchive.Id, model.VisibleItems.Single().Id);
        Assert.IsTrue(model.CanSearch);
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

    [TestMethod]
    public void LateAutosaveCompletionDoesNotMarkANewerDraftSavedOrResetItsEditor()
    {
        var original = ControlledStorage.Record("Original", "Original body");
        var storage = new ControlledStorage(original);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Body.Text = "First autosave snapshot";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => storage.Saves.Count == 1);
        Assert.IsTrue(model.IsSaving);
        Assert.IsTrue(model.CanEdit);

        model.Body.Text = "Newer draft while save is pending";
        model.Body.SetSelection(6, 11);
        fixture.Drain();
        Assert.IsTrue(fixture.Autosave.HasPending);
        storage.CompleteSave(0);
        fixture.Until(() => !model.IsSaving);

        Assert.AreEqual("Newer draft while save is pending", model.Body.Text);
        Assert.AreEqual(6, model.Body.Anchor);
        Assert.AreEqual(11, model.Body.Caret);
        Assert.IsTrue(model.Body.CanUndo);
        Assert.IsTrue(model.IsDirty);
        Assert.AreEqual("First autosave snapshot", model.Selected.Value!.Body);

        fixture.Autosave.Fire();
        fixture.Until(() => storage.Saves.Count == 2);
        Assert.AreEqual(2, storage.Saves[1].Draft.ExpectedRevision);
        storage.CompleteSave(1);
        fixture.Until(() => !model.IsSaving && !model.IsDirty);
        Assert.AreEqual("Newer draft while save is pending", model.Selected.Value!.Body);
    }

    [TestMethod]
    public void RevertingToPersistedDraftWhileAutosaveIsInFlightFlushesTheReversionOnClose()
    {
        var original = ControlledStorage.Record("Original", "Original body");
        var storage = new ControlledStorage(original);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Body.Text = "Pending different body";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => storage.Saves.Count == 1);

        model.Body.Text = original.Body;
        fixture.Drain();
        Assert.IsTrue(model.IsDirty);

        var close = model.PrepareCloseAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);

        storage.CompleteSave(0);
        fixture.Until(() => storage.Saves.Count == 2);
        Assert.AreEqual(original.Body, storage.Saves[1].Draft.Body);
        Assert.AreEqual(2, storage.Saves[1].Draft.ExpectedRevision);

        storage.CompleteSave(1);
        fixture.Until(() => close.IsCompleted);
        Assert.IsTrue(close.Result);
        Assert.AreEqual(original.Body, storage.Current.Body);
    }

    [TestMethod]
    public void RevertingToPersistedDraftBeforeDebouncePublishesCleanReactiveWorkspaceState()
    {
        var original = ControlledStorage.Record("Original", "Original body");
        var storage = new ControlledStorage(original);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        var observations = new List<(bool Dirty, string Status, bool SaveEnabled)>();
        fixture.Observe(() =>
            observations.Add((model.IsDirty, model.StatusText, model.SaveCommand.IsEnabled))
        );
        fixture.Drain();
        observations.Clear();

        model.Body.Text = "Draft B";
        fixture.Drain();
        Assert.IsTrue(fixture.Autosave.HasPending);
        Assert.AreEqual(0, storage.Saves.Count);
        Assert.IsTrue(observations[^1].Dirty);
        Assert.AreEqual("Saving changes...", observations[^1].Status);
        Assert.IsTrue(observations[^1].SaveEnabled);

        model.Body.Undo();
        fixture.Drain();

        Assert.AreEqual(original.Body, model.Body.Text);
        Assert.IsFalse(fixture.Autosave.HasPending);
        Assert.AreEqual(0, storage.Saves.Count);
        Assert.IsFalse(observations[^1].Dirty);
        Assert.AreEqual("Saved on this device", observations[^1].Status);
        Assert.IsFalse(observations[^1].SaveEnabled);
    }

    [TestMethod]
    public void AutosaveValidationFailureKeepsTheDraftAndRetrySavesTheCorrection()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "A valid starting note";
        fixture.Execute(model.CaptureCommand);

        model.Title.Text = "";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => model.HasValidationError && !model.IsSaving);

        Assert.IsTrue(model.IsDirty);
        Assert.IsTrue(model.DiscardDraftCommand.IsEnabled);
        Assert.AreEqual("", model.Title.Text);
        StringAssert.Contains(model.StatusText, "Give this note a title");

        model.Url.Text = "https://example.com/still-show-save-failure";
        fixture.Execute(model.OpenLinkCommand);
        Assert.IsTrue(model.HasValidationError);
        StringAssert.Contains(model.StatusText, "Give this note a title");

        model.Title.Text = "Corrected after autosave failure";
        fixture.Drain();
        fixture.Execute(model.SaveCommand);
        Assert.IsFalse(model.IsDirty);
        Assert.AreEqual("Corrected after autosave failure", model.Selected.Value!.Title);
    }

    [TestMethod]
    public void InvalidDraftAutosavesWithoutStorageErrorAndSurvivesNavigation()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "First valid note";
        fixture.Execute(model.CaptureCommand);
        var first = model.Selected.Value!.Id;

        model.Title.Text = "";
        model.Url.Text = "https://";
        model.Body.Text = "Incomplete but recoverable";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => !model.IsSaving);

        Assert.IsFalse(model.HasError);
        Assert.IsFalse(model.CanOpenLink);
        StringAssert.Contains(model.StatusText, "Draft saved");

        model.Capture.Text = "Second note";
        fixture.Execute(model.CaptureCommand);
        Assert.AreNotEqual(first, model.Selected.Value!.Id);

        model.Select(first);
        fixture.Until(() => !model.IsBusy && model.Selected.Value?.Id == first);
        Assert.AreEqual("", model.Title.Text);
        Assert.AreEqual("https://", model.Url.Text);
        Assert.AreEqual("Incomplete but recoverable", model.Body.Text);
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void CollectionNavigationRetainsPendingDraftAndReportsRecoveryWriteFailureTruthfully()
    {
        var inbox = ControlledStorage.Record("Inbox", "saved");
        var archived = ControlledStorage.Record("Archive", "saved") with { IsArchived = true };
        var storage = new PendingRecoveryStorage([inbox, archived]);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Title.Text = "";
        model.Body.Text = "Retained while recovery write is pending";
        model.ShowArchive();

        Assert.IsTrue(
            model.ShowArchived.Value,
            "Cached collection switch waited for recovery I/O."
        );
        Assert.AreEqual(archived.Id, model.Selected.Value?.Id);
        Assert.IsFalse(model.HasDurableRecoveryDraft);

        model.ShowInbox();
        Assert.AreEqual(inbox.Id, model.Selected.Value?.Id);
        Assert.AreEqual("", model.Title.Text);
        Assert.AreEqual("Retained while recovery write is pending", model.Body.Text);
        Assert.IsFalse(model.HasDurableRecoveryDraft);

        storage.FailRecovery(new IOException("Recovery disk unavailable."));
        fixture.Until(() => model.HasError);
        Assert.AreEqual("Could not save", model.ErrorHeading);
        StringAssert.Contains(model.StatusText, "Recovery disk unavailable");
        Assert.IsFalse(model.HasDurableRecoveryDraft);
        Assert.IsTrue(model.HasRecoveryDraft);
    }

    [TestMethod]
    public void PendingRecoveryWritesAreSerializedAndLatestDraftWinsAcrossCollectionSwitches()
    {
        var inbox = ControlledStorage.Record("Inbox", "saved");
        var archived = ControlledStorage.Record("Archive", "saved") with { IsArchived = true };
        var storage = new SerializedRecoveryStorage([inbox, archived]);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Title.Text = "";
        model.Body.Text = "version one";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => storage.Saves.Count == 1);

        model.Body.Text = "version two";
        fixture.Drain();
        model.ShowArchive();
        Assert.AreEqual(1, storage.Saves.Count, "A second write bypassed the per-note writer.");

        storage.Complete(0);
        fixture.Until(() => storage.Saves.Count == 2);
        Assert.AreEqual("version two", storage.Saves[1].Draft.Body);
        storage.Complete(1);
        fixture.Until(() => !model.IsSaving);

        model.ShowInbox();
        Assert.AreEqual("version two", model.Body.Text);
        Assert.IsTrue(model.HasDurableRecoveryDraft);
    }

    [TestMethod]
    public void StaleRecoveredDraftConflictsUntilExplicitRetry()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        var seedTask = NoteStore.OpenAsync(fixture.DatabasePath);
        fixture.Pump(seedTask);
        var seed = seedTask.Result;
        var firstTask = seed.SaveAsync(new(id, NoteKind.Note, "First", null, "one", 0));
        fixture.Pump(firstTask);
        var first = firstTask.Result;
        fixture.Pump(
            seed.SaveRecoveryDraftAsync(new(id, NoteKind.Note, "", null, "draft", first.Revision))
        );
        var secondTask = seed.SaveAsync(
            new(id, NoteKind.Note, "Other window", null, "two", first.Revision)
        );
        fixture.Pump(secondTask);
        fixture.Pump(seed.DisposeAsync().AsTask());

        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.AreEqual("draft", model.Body.Text);
        model.Title.Text = "Corrected recovery";
        fixture.Drain();
        Assert.IsTrue(model.SaveCommand.TryExecute());
        fixture.Until(() => !model.SaveCommand.IsBusy && model.HasError);
        StringAssert.Contains(model.StatusText, "changed on disk");

        var inspectTask = NoteStore.OpenAsync(fixture.DatabasePath);
        fixture.Pump(inspectTask);
        var inspect = inspectTask.Result;
        var unchangedTask = inspect.GetAsync(id);
        fixture.Pump(unchangedTask);
        Assert.AreEqual("Other window", unchangedTask.Result!.Title);
        fixture.Pump(inspect.DisposeAsync().AsTask());

        fixture.Execute(model.RetryCommand);
        Assert.AreEqual("Corrected recovery", model.Selected.Value!.Title);
        Assert.IsFalse(model.HasRecoveryDraft);
    }

    [TestMethod]
    public void RetriedOffscreenRecoveryIsReconciledAsDurableAndAllowsClose()
    {
        var first = ControlledStorage.Record("First", "saved");
        var second = ControlledStorage.Record("Second", "saved") with { IsArchived = true };
        var storage = new RetryableRecoveryStorage([first, second]);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Title.Text = "";
        model.Body.Text = "recover me";
        fixture.Drain();
        fixture.Autosave.Fire();
        fixture.Until(() => storage.Pending is not null);
        storage.Fail(new IOException("disk unavailable"));
        fixture.Until(() => model.HasError);

        model.ShowArchive();
        Assert.AreEqual(second.Id, model.Selected.Value?.Id);
        fixture.Execute(model.RetryCommand);
        model.ShowInbox();
        Assert.AreEqual(first.Id, model.Selected.Value?.Id);
        Assert.AreEqual("recover me", model.Body.Text);
        Assert.IsTrue(model.HasDurableRecoveryDraft);
        var close = model.PrepareCloseAsync().AsTask();
        fixture.Pump(close);
        Assert.IsTrue(close.Result);
    }

    [TestMethod]
    public void RecoveredDraftCanBeExplicitlyDiscardedToLastValidSave()
    {
        using var fixture = new Fixture();
        var seed = NoteStore.OpenAsync(fixture.DatabasePath);
        fixture.Pump(seed);
        var id = Guid.NewGuid();
        var saved = seed.Result.SaveAsync(
            new NoteDraft(id, NoteKind.Link, "Last valid", "https://example.com", "saved", 0)
        );
        fixture.Pump(saved);
        fixture.Pump(
            seed.Result.SaveRecoveryDraftAsync(
                new NoteDraft(id, NoteKind.Link, "", "https://", "recovered", saved.Result.Revision)
            )
        );
        fixture.Pump(seed.Result.DisposeAsync().AsTask());

        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.AreEqual("", model.Title.Text);
        Assert.AreEqual("recovered", model.Body.Text);
        Assert.IsTrue(model.DiscardDraftCommand.IsEnabled);

        fixture.Execute(model.DiscardDraftCommand);
        Assert.AreEqual("Last valid", model.Title.Text);
        Assert.AreEqual("https://example.com", model.Url.Text);
        Assert.AreEqual("saved", model.Body.Text);
        Assert.IsFalse(model.IsDirty);
        Assert.IsFalse(model.DiscardDraftCommand.IsEnabled);
    }

    [TestMethod]
    public void CollectionSwitchRestoresSelectionQueryAndScrollBeforeRefreshCompletes()
    {
        var inboxOne = ControlledStorage.Record("Inbox one", "one");
        var inboxTwo = ControlledStorage.Record("Inbox two", "two");
        var archiveOne = ControlledStorage.Record("Archive one", "one") with { IsArchived = true };
        var archiveTwo = ControlledStorage.Record("Archive two", "two") with { IsArchived = true };
        var all = new[] { inboxOne, inboxTwo, archiveOne, archiveTwo };
        var storage = new DelayedReloadStorage(all, all);
        using var fixture = new Fixture(storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        model.Select(inboxTwo.Id);
        fixture.Until(() => model.Selected.Value?.Id == inboxTwo.Id);
        model.Search.Text = "Inbox two";
        model.CollectionViewport.Offset = new ScrollOffset(0, 120);

        model.ShowArchive();
        fixture.Drain();
        Assert.IsTrue(model.ShowArchived.Value);
        Assert.AreEqual("", model.Query);
        Assert.AreEqual(archiveOne.Id, model.Selected.Value?.Id);
        Assert.AreEqual(new ScrollOffset(0, 0), model.CollectionViewport.Offset);
        Assert.IsTrue(storage.ReloadPending);

        model.Search.Text = "Archive two";
        model.Select(archiveTwo.Id);
        fixture.Until(() => model.Selected.Value?.Id == archiveTwo.Id);
        model.CollectionViewport.Offset = new ScrollOffset(0, 240);
        storage.CompleteReload();
        fixture.Until(() => !storage.ReloadPending);

        model.ShowInbox();
        fixture.Drain();
        Assert.IsFalse(model.ShowArchived.Value);
        Assert.AreEqual("Inbox two", model.Query);
        Assert.AreEqual(inboxTwo.Id, model.Selected.Value?.Id);
        Assert.AreEqual(new ScrollOffset(0, 120), model.CollectionViewport.Offset);

        model.ShowArchive();
        fixture.Drain();
        Assert.AreEqual("Archive two", model.Query);
        Assert.AreEqual(archiveTwo.Id, model.Selected.Value?.Id);
        Assert.AreEqual(new ScrollOffset(0, 240), model.CollectionViewport.Offset);
    }

    [TestMethod]
    public void RecordTargetedArchiveDoesNotNavigateAwayFromTheActiveEditor()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "Keep open";
        fixture.Execute(model.CaptureCommand);
        var active = model.Selected.Value!.Id;
        model.Capture.Text = "Archive from row menu";
        fixture.Execute(model.CaptureCommand);
        var target = model.Selected.Value!.Id;
        model.Open(active);
        fixture.Until(() => model.Selected.Value?.Id == active && !model.IsBusy);
        model.Body.Text = "Active editor draft stays here";

        model.SetArchived(target, archived: true);
        fixture.Until(() => !model.IsBusy);

        Assert.AreEqual(active, model.Selected.Value?.Id);
        Assert.AreEqual("Active editor draft stays here", model.Body.Text);
        Assert.IsFalse(model.VisibleItems.Any(item => item.Id == target));
        Assert.IsFalse(model.CanOpenRecordLink(target));
    }

    [TestMethod]
    public void OpenLinkUsesTheInjectedBoundedServiceWithoutChangingTheDraft()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "https://example.com/notes?id=81";
        fixture.Execute(model.CaptureCommand);
        model.Url.Text = "https://example.com/edited-before-save";
        model.Body.Text = "Keep this unsaved edit and its undo history.";
        model.Body.SetSelection(5, 9);

        Assert.IsTrue(model.CanOpenLink);
        fixture.Execute(model.OpenLinkCommand);

        Assert.AreEqual(
            new Uri("https://example.com/edited-before-save"),
            fixture.LinkOpener.Opened
        );
        Assert.AreEqual("Keep this unsaved edit and its undo history.", model.Body.Text);
        Assert.AreEqual(5, model.Body.Anchor);
        Assert.AreEqual(9, model.Body.Caret);
        Assert.IsTrue(model.Body.CanUndo);
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void OpenLinkFailureHasAnOperationSpecificHeadingAndDoesNotBrowseAgain()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Capture.Text = "https://example.com/failure";
        fixture.Execute(model.CaptureCommand);
        fixture.LinkOpener.Failure = new IOException("No registered browser accepted the link.");

        fixture.Execute(model.OpenLinkCommand);

        Assert.AreEqual(1, fixture.LinkOpener.CallCount);
        Assert.AreEqual("Could not open link", model.ErrorHeading);
        StringAssert.Contains(model.StatusText, "No registered browser");
    }

    [TestMethod]
    public void SystemLinkOpenerRejectsUnsupportedSchemesBeforePlatformLaunch()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            SystemExternalLinkOpener.Validate(new Uri("file:///C:/notes.txt"))
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            SystemExternalLinkOpener.Validate(new Uri("mailto:notes@example.com"))
        );
    }

    [TestMethod]
    public void CrashReportIsBoundedAndOmitsExceptionMessagesAndNoteContent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "light-notes-crash-report-" + Guid.NewGuid().ToString("N")
        );
        const string privateNote = "private note body must not enter diagnostics";
        try
        {
            Exception error;
            try
            {
                throw new InvalidOperationException(
                    privateNote,
                    new ArgumentException("private nested title")
                );
            }
            catch (Exception captured)
            {
                error = captured;
            }

            var path = Program.TryWriteCrashReport(directory, error);
            Assert.IsNotNull(path);
            Assert.AreEqual(Path.Combine(directory, "last-crash.txt"), path);
            var report = File.ReadAllText(path);
            Assert.IsTrue(report.Contains(typeof(InvalidOperationException).FullName!));
            Assert.IsTrue(report.Contains(typeof(ArgumentException).FullName!));
            Assert.IsTrue(
                report.Contains(nameof(CrashReportIsBoundedAndOmitsExceptionMessagesAndNoteContent))
            );
            Assert.IsFalse(report.Contains(privateNote, StringComparison.Ordinal));
            Assert.IsFalse(report.Contains("private nested title", StringComparison.Ordinal));
            Assert.IsTrue(report.Length <= 64 * 1024);
            Assert.IsNull(Program.TryWriteCrashReport("", error));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
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

        public Fixture(INoteWorkspaceStorage? storage = null)
        {
            SetSynchronizationContext(this);
            _scope = _graph.CreateScope("workspace-test");
            LinkOpener = new();
            Autosave = new();
            Model = storage is null
                ? new(_scope, DatabasePath, LinkOpener, Autosave)
                : new(_scope, DatabasePath, LinkOpener, Autosave, _ => Task.FromResult(storage));
        }

        public Fixture(Exception startupFailure)
        {
            SetSynchronizationContext(this);
            _scope = _graph.CreateScope("workspace-test");
            LinkOpener = new();
            Autosave = new();
            Model = new(
                _scope,
                DatabasePath,
                LinkOpener,
                Autosave,
                _ => Task.FromException<INoteWorkspaceStorage>(startupFailure)
            );
        }

        public string DatabasePath => Path.Combine(_directory, "notes.db");
        public FakeLinkOpener LinkOpener { get; }
        public ManualDebounceScheduler Autosave { get; }
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

        public void Drain() => _graph.Drain();

        public void Observe(Action callback) => _scope.Effect(callback, "workspace-observer");

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

    private sealed class FakeLinkOpener : IExternalLinkOpener
    {
        public Uri? Opened { get; private set; }

        public int CallCount { get; private set; }

        public Exception? Failure { get; set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened = uri;
            CallCount++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class ManualDebounceScheduler : IDebounceScheduler
    {
        private Action? _pending;

        public bool HasPending => _pending is not null;

        public int RestartCount { get; private set; }

        public void Restart(TimeSpan delay, Action callback)
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(750), delay);
            _pending = callback;
            RestartCount++;
        }

        public void Cancel() => _pending = null;

        public void Fire()
        {
            var callback = _pending ?? throw new InvalidOperationException("Nothing is scheduled.");
            _pending = null;
            callback();
        }

        public void Dispose() => Cancel();
    }

    private sealed class DelayedReloadStorage(
        IReadOnlyList<NoteRecord> initial,
        IReadOnlyList<NoteRecord> reloaded
    ) : INoteWorkspaceStorage
    {
        private readonly TaskCompletionSource<IReadOnlyList<NoteRecord>> _reload = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _listCalls;

        public bool ReloadPending => _listCalls > 1 && !_reload.Task.IsCompleted;

        public bool HasUnresolvedWriteFailures => false;

        public void CompleteReload() => _reload.SetResult(reloaded);

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            ++_listCalls == 1 ? Task.FromResult(initial) : _reload.Task;

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(reloaded.FirstOrDefault(item => item.Id == id));

        public Task<NoteRecord> SaveAsync(NoteDraft draft) => throw new NotSupportedException();

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(0, 0, 0));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledStorage(NoteRecord initial) : INoteWorkspaceStorage
    {
        public List<PendingSave> Saves { get; } = [];

        public NoteRecord Current { get; private set; } = initial;

        public bool HasUnresolvedWriteFailures => false;

        public Task<NoteRecord> SaveAsync(NoteDraft draft)
        {
            var pending = new PendingSave(draft);
            Saves.Add(pending);
            return pending.Completion.Task;
        }

        public void CompleteSave(int index)
        {
            var pending = Saves[index];
            Current = Current with
            {
                Kind = pending.Draft.Kind,
                Title = pending.Draft.Title,
                Url = pending.Draft.Url,
                Body = pending.Draft.Body,
                Revision = Current.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            pending.Completion.SetResult(Current);
        }

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(id == Current.Id ? Current : null);

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult<IReadOnlyList<NoteRecord>>([Current]);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(0, 0, 0));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public static NoteRecord Record(string title, string body)
        {
            var now = DateTimeOffset.UtcNow;
            return new(Guid.NewGuid(), NoteKind.Note, title, null, body, false, 1, now, now);
        }

        public sealed record PendingSave(NoteDraft Draft)
        {
            public TaskCompletionSource<NoteRecord> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class SerializedRecoveryStorage(IReadOnlyList<NoteRecord> records)
        : INoteWorkspaceStorage
    {
        public List<PendingRecovery> Saves { get; } = [];
        public bool HasUnresolvedWriteFailures => false;

        public Task<NoteRecord> SaveAsync(NoteDraft draft) => throw new NotSupportedException();

        public Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(NoteDraft draft)
        {
            var pending = new PendingRecovery(draft);
            Saves.Add(pending);
            return pending.Completion.Task;
        }

        public void Complete(int index)
        {
            var pending = Saves[index];
            pending.Completion.SetResult(
                new(
                    pending.Draft.Id,
                    pending.Draft.Kind,
                    pending.Draft.Title,
                    pending.Draft.Url,
                    pending.Draft.Body,
                    pending.Draft.ExpectedRevision!.Value,
                    DateTimeOffset.UtcNow
                )
            );
        }

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(records.FirstOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult(records);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(0, 0, 0));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public sealed record PendingRecovery(NoteDraft Draft)
        {
            public TaskCompletionSource<NoteRecoveryDraft> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class RetryableRecoveryStorage(IReadOnlyList<NoteRecord> records)
        : INoteWorkspaceStorage
    {
        private NoteRecoveryDraft? _durable;
        private TaskCompletionSource<NoteRecoveryDraft>? _completion;
        public NoteDraft? Pending { get; private set; }
        public bool HasUnresolvedWriteFailures { get; private set; }

        public Task<NoteRecord> SaveAsync(NoteDraft draft) => throw new NotSupportedException();

        public Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(NoteDraft draft)
        {
            Pending = draft;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }

        public void Fail(Exception error)
        {
            HasUnresolvedWriteFailures = true;
            _completion!.SetException(error);
        }

        public Task<WriteRetryResult> RetryFailedWritesAsync()
        {
            var pending = Pending!;
            _durable = new(
                pending.Id,
                pending.Kind,
                pending.Title,
                pending.Url,
                pending.Body,
                pending.ExpectedRevision!.Value,
                DateTimeOffset.UtcNow
            );
            HasUnresolvedWriteFailures = false;
            return Task.FromResult(new WriteRetryResult(1, 1, 0));
        }

        public Task<IReadOnlyList<NoteRecoveryDraft>> ListRecoveryDraftsAsync() =>
            Task.FromResult<IReadOnlyList<NoteRecoveryDraft>>(_durable is null ? [] : [_durable]);

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(records.FirstOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult(records);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(!HasUnresolvedWriteFailures);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PendingRecoveryStorage(IReadOnlyList<NoteRecord> records)
        : INoteWorkspaceStorage
    {
        private readonly TaskCompletionSource<NoteRecoveryDraft> _recovery = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private NoteDraft? _pending;

        public bool HasUnresolvedWriteFailures { get; private set; }

        public Task<NoteRecord> SaveAsync(NoteDraft draft) => throw new NotSupportedException();

        public Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(NoteDraft draft)
        {
            _pending = draft;
            return _recovery.Task;
        }

        public void FailRecovery(Exception error)
        {
            Assert.IsNotNull(_pending);
            HasUnresolvedWriteFailures = true;
            _recovery.SetException(error);
        }

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(records.FirstOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult(records);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(1, 0, 1));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellableCloseStorage : INoteWorkspaceStorage
    {
        public bool HasUnresolvedWriteFailures => false;
        public bool CloseStarted { get; private set; }
        public CancellationToken CloseToken { get; private set; }

        public Task<NoteRecord> SaveAsync(NoteDraft draft) => throw new NotSupportedException();

        public Task<NoteRecord?> GetAsync(Guid id) => Task.FromResult<NoteRecord?>(null);

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult<IReadOnlyList<NoteRecord>>([]);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(0, 0, 0));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public async Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default)
        {
            CloseToken = cancellationToken;
            CloseStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
