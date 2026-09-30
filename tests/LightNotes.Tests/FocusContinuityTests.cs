using LightNotes.Storage;
using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class ShellPresentationTests
{
    [TestMethod]
    public void ResponsiveCollapseRehomesFocusWithoutLosingDraftOrReplayingOnReturn()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        using var composition = new Composition(fixture.Graph, "notes-focus-continuity");
        composition.ConfigureImages(new ImageCache(new SkiaImagePreparer()));
        composition.Input.FocusRecovery = FocusRecoveryPolicy.NearestAvailable;
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();
        using var wide = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-wide",
            1180,
            760,
            1,
            export: false
        );
        var search = Semantic(composition, "Search saved items", SemanticRole.TextField);
        var searchSession = model.Search;
        var titleSession = model.Title;
        var selectedId = model.Selected.Value!.Id;
        model.Select(selectedId);
        using var selected = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-selected",
            1180,
            760,
            1,
            export: false
        );
        Assert.IsTrue(
            composition.Input.FocusSemantic(
                new ElementIdentity(search.Identity.CompositionEpoch, search.Identity.ElementId)
            )
        );
        using var focused = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-search",
            1180,
            760,
            1,
            export: false
        );

        using var narrow = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-narrow",
            560,
            760,
            1,
            export: false
        );
        Assert.IsTrue(model.ShowEditor, "The fixture must collapse the collection pane.");
        AssertSemantic(composition, search, false, "Hidden collection search");
        var replacement = composition.Input.FocusedElement;
        Assert.IsNotNull(replacement, "The visible pane should receive focus.");
        Assert.AreNotEqual(search.Identity.ElementId, replacement.Value.ElementId);
        Assert.IsTrue(
            Descendants(composition.SemanticSnapshot())
                .Any(node => node.Identity.ElementId == replacement.Value.ElementId),
            "Recovery must select a current accessible target."
        );

        var title = Semantic(composition, "Title", SemanticRole.TextField);
        var titleIdentity = new ElementIdentity(
            title.Identity.CompositionEpoch,
            title.Identity.ElementId
        );
        Assert.IsTrue(composition.Input.FocusSemantic(titleIdentity));
        using var titleFocused = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-title",
            560,
            760,
            1,
            export: false
        );
        using var returned = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-returned",
            1180,
            760,
            1,
            export: false
        );
        Assert.AreEqual(
            titleIdentity,
            composition.Input.FocusedElement,
            "Restoring the collection must not steal focus back to search."
        );
        Assert.AreSame(searchSession, model.Search);
        Assert.AreSame(titleSession, model.Title);
        Assert.AreEqual(selectedId, model.Selected.Value!.Id);
    }

    [TestMethod]
    public void SaveShortcutKeepsFocusedEditorAndAcceptsTypingWhileStorageIsPending()
    {
        var storage = new HeldEditorSaveStorage();
        using var fixture = new Fixture(storage: storage);
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        using var composition = new Composition(fixture.Graph, "notes-pending-save-focus");
        composition.ConfigureImages(new ImageCache(new SkiaImagePreparer()));
        composition.Input.FocusRecovery = FocusRecoveryPolicy.NearestAvailable;
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, RoutedAppView(model));
        using var renderer = new SkiaSceneRenderer();
        try
        {
            using var initial = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-initial",
                1180,
                760,
                1,
                export: false
            );
            var field = Descendants(composition.SemanticSnapshot())
                .Single(node => node.Name == "Notes" && node.Text is not null);
            var identity = new ElementIdentity(
                field.Identity.CompositionEpoch,
                field.Identity.ElementId
            );
            var session = model.Body;
            Assert.IsTrue(composition.Input.FocusSemantic(identity));
            session.SetSelection(10, 10);
            using var focused = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-focused",
                1180,
                760,
                1,
                export: false
            );
            Assert.IsTrue(
                composition.Input.DispatchText(new(TextInputKind.Commit, " first")).Handled
            );
            fixture.Drain();
            Assert.AreEqual("Saved body first", session.Text);
            session.SetSelection(6, 10);
            using var dirty = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-dirty",
                1180,
                760,
                1,
                export: false
            );

            Assert.IsTrue(
                composition
                    .Input.DispatchKey(new(KeyCommandKind.Down, Key.S, KeyModifiers.Control))
                    .Handled
            );
            fixture.Until(() => storage.PendingDraft is not null);
            Assert.IsTrue(model.IsSaving);
            Assert.IsTrue(model.SaveCommand.IsBusy);
            var pendingDraft = storage.PendingDraft;
            Assert.IsNotNull(pendingDraft);
            Assert.AreEqual("Saved body first", pendingDraft.Body);
            using var pending = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-pending",
                1180,
                760,
                1,
                export: false
            );
            Assert.AreEqual(identity, composition.Input.FocusedElement);
            Assert.AreSame(session, model.Body);
            Assert.AreEqual(6, session.Anchor);
            Assert.AreEqual(10, session.Caret);
            Assert.IsTrue(model.CanEdit);

            Assert.IsTrue(
                composition.Input.DispatchText(new(TextInputKind.Commit, "newer")).Handled,
                "The focused field must accept input while its save is pending."
            );
            fixture.Drain();
            Assert.AreEqual("Saved newer first", session.Text);
            Assert.AreEqual(11, session.Anchor);
            Assert.AreEqual(11, session.Caret);
            Assert.IsTrue(model.IsSaving, "Storage must remain held through the follow-up input.");
            using var typed = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-typed",
                1180,
                760,
                1,
                export: false
            );
            Assert.AreEqual(identity, composition.Input.FocusedElement);

            storage.ReleaseFirst();
            fixture.Until(() => !model.IsSaving && !model.SaveCommand.IsBusy);
            using var completed = Scenario(
                fixture,
                composition,
                renderer,
                model,
                "save-focus-completed",
                1180,
                760,
                1,
                export: false
            );
            Assert.AreSame(session, model.Body);
            Assert.AreEqual(identity, composition.Input.FocusedElement);
            Assert.AreEqual("Saved newer first", session.Text);
            Assert.AreEqual(11, session.Anchor);
            Assert.AreEqual(11, session.Caret);
            Assert.IsTrue(session.CanUndo);
            Assert.IsTrue(model.IsDirty, "The older completion cannot mark the newer input saved.");
            Assert.AreEqual("Saved body first", model.Selected.Value!.Body);
        }
        finally
        {
            storage.ReleaseFirst();
        }
    }

    private sealed class HeldEditorSaveStorage : INoteWorkspaceStorage
    {
        private readonly TaskCompletionSource<NoteRecord> _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private bool _released;
        private NoteRecord _current = new(
            Guid.NewGuid(),
            NoteKind.Note,
            "Focused note",
            null,
            "Saved body",
            false,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch
        );

        public NoteDraft? PendingDraft { get; private set; }
        public bool HasUnresolvedWriteFailures => false;

        public Task<NoteRecord> SaveAsync(NoteDraft draft)
        {
            if (!_released)
            {
                Assert.IsNull(PendingDraft, "Only one save may be outstanding.");
                PendingDraft = draft;
                return _first.Task;
            }
            return Task.FromResult(Apply(draft));
        }

        public void ReleaseFirst()
        {
            if (_released)
                return;
            _released = true;
            if (PendingDraft is { } draft)
                _first.SetResult(Apply(draft));
        }

        private NoteRecord Apply(NoteDraft draft) =>
            _current = _current with
            {
                Title = draft.Title,
                Body = draft.Body,
                Url = draft.Url,
                Kind = draft.Kind,
                Revision = _current.Revision + 1,
                UpdatedAt = _current.UpdatedAt.AddSeconds(1),
            };

        public Task<NoteRecord?> GetAsync(Guid id) =>
            Task.FromResult<NoteRecord?>(id == _current.Id ? _current : null);

        public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
            Task.FromResult<IReadOnlyList<NoteRecord>>([_current]);

        public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
            throw new NotSupportedException();

        public Task<WriteRetryResult> RetryFailedWritesAsync() =>
            Task.FromResult(new WriteRetryResult(0, 0, 0));

        public Task BackupAsync(string destinationPath) => throw new NotSupportedException();

        public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
