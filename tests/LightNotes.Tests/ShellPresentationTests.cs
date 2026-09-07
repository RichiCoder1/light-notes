using System.Collections.Concurrent;
using LightNotes.ReviewFixtures;
using Lucent.Core;
using Lucent.Renderer.Skia;
using SkiaSharp;

namespace LightNotes.Tests;

[TestClass]
public sealed class ShellPresentationTests
{
    [TestMethod]
    public void ResponsiveShellRendersRealContentAndRetainsWorkspaceState()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.IsTrue(model.IsReady, "Review workspace did not finish loading.");
        Assert.IsTrue(model.VisibleItems.Count >= 20, "Review records did not load.");

        using var composition = new Composition(fixture.Graph, "light-notes-shell-review");
        var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();

        var selectedId = model.Selected.Value!.Id;
        var titleSession = model.Title;
        var bodySession = model.Body;
        var searchSession = model.Search;

        model.Title.Text = "Responsive review draft";
        model.Body.Text = "Draft selection survives wide, medium, and compact layouts. 😀";
        model.Body.SetSelection(0, 5);

        var wide = Scenario(fixture, composition, renderer, model, "wide", 1180, 760, 1);
        var capture = Semantic(composition, "Capture a link or thought", SemanticRole.TextField);
        var navigation = SemanticStartingWith(composition, "Inbox", SemanticRole.ListItem);
        var search = Semantic(composition, "Search saved items", SemanticRole.TextField);
        var title = Semantic(composition, "Title", SemanticRole.TextField);
        var body = SemanticText(composition, "Notes");
        var url = Semantic(composition, "Web address", SemanticRole.TextField);
        var notesList = Semantic(composition, "Saved notes", SemanticRole.List);
        var save = Semantic(composition, "Save now", SemanticRole.Button);
        Assert.AreEqual(NoteWorkspaceLayout.Wide, model.Layout);
        AssertBounds(wide, search, expectedWidth: 288, message: "wide collection width");
        AssertBounds(wide, navigation, maximumX: 184, message: "wide navigation track");
        AssertPresent(wide, title, true, "wide editor");
        AssertNearBottom(wide, notesList, 760, "wide list viewport");
        AssertNearBottom(wide, save, 760, "wide editor actions");
        AssertSemantic(composition, search, true, "wide search");
        AssertSemantic(composition, title, true, "wide title");

        var scrollAnchorLabel = NotePresentation.RowLabel(model.VisibleItems[4]);
        var scrollAnchor = Semantic(composition, scrollAnchorLabel, SemanticRole.ListItem);
        var initialScrollAnchorY = wide
            .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
            .Bounds.Y;
        var scrollTargetBounds = wide
            .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
            .Bounds;
        var wheel = composition.Input.DispatchWheel(
            new(
                scrollTargetBounds.X + scrollTargetBounds.Width / 2f,
                scrollTargetBounds.Y + scrollTargetBounds.Height / 2f,
                0,
                144
            )
        );
        Assert.IsTrue(
            wheel.Handled,
            $"Review list did not accept wheel input. Bounds={scrollTargetBounds}; "
                + $"status={wheel.Status}; rejection={wheel.Rejection}; target={wheel.Target}; "
                + composition.Input.Dump()
        );
        wide = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "wide-scrolled",
            1180,
            760,
            1,
            export: false
        );
        scrollAnchor = Semantic(composition, scrollAnchorLabel, SemanticRole.ListItem);
        var retainedScrollAnchorY = wide
            .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
            .Bounds.Y;
        Assert.IsTrue(
            retainedScrollAnchorY < initialScrollAnchorY - 70f,
            "Wheel input did not visibly scroll the review list."
        );

        var medium = Scenario(fixture, composition, renderer, model, "medium", 900, 760, 1);
        Assert.AreEqual(NoteWorkspaceLayout.Medium, model.Layout);
        AssertBounds(medium, search, expectedWidth: 268, message: "medium collection width");
        AssertBounds(medium, navigation, maximumX: 64, message: "medium navigation rail");
        AssertPresent(medium, title, true, "medium editor");
        AssertNearBottom(medium, notesList, 760, "medium list viewport");
        AssertNearBottom(medium, save, 760, "medium editor actions");
        AssertInsideViewport(
            medium,
            900,
            760,
            save,
            Semantic(composition, "Archive", SemanticRole.Button),
            Semantic(composition, "Backup", SemanticRole.Button)
        );
        scrollAnchor = Semantic(composition, scrollAnchorLabel, SemanticRole.ListItem);
        Assert.AreEqual(
            retainedScrollAnchorY,
            medium
                .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
                .Bounds.Y,
            0.01f,
            "Medium resize reset list scroll."
        );

        _ = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "compact-bucket",
            560,
            760,
            1,
            export: false
        );
        Assert.AreEqual(NoteWorkspaceLayout.Compact, model.Layout);
        model.Select(selectedId);
        _ = Scenario(fixture, composition, renderer, model, "compact-editor", 560, 760, 1);
        Assert.IsTrue(
            model.ShowEditor,
            "Selecting the active item did not open its compact editor."
        );
        AssertSemantic(composition, navigation, false, "compact navigation");
        AssertSemantic(composition, search, false, "compact hidden collection");
        AssertSemantic(composition, title, true, "compact title");

        model.BackToCollection();
        var compactCollection = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "compact-collection",
            560,
            760,
            1
        );
        Assert.IsTrue(
            model.ShowCollection && !model.ShowEditor,
            "Compact Back did not show one collection pane."
        );
        AssertPresent(compactCollection, search, true, "compact collection");
        AssertPresent(compactCollection, title, false, "compact hidden editor");
        AssertSemantic(composition, search, true, "compact search");
        AssertSemantic(composition, title, false, "compact hidden title");
        AssertPresent(
            compactCollection,
            Semantic(composition, "Archive", SemanticRole.Button),
            true,
            "compact archive destination"
        );
        scrollAnchor = Semantic(composition, scrollAnchorLabel, SemanticRole.ListItem);
        Assert.AreEqual(
            retainedScrollAnchorY,
            compactCollection
                .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
                .Bounds.Y,
            0.01f,
            "Compact Back reset list scroll."
        );
        Assert.AreEqual(
            search.Identity.ElementId,
            composition.Input.FocusedElement?.ElementId,
            "Compact Back did not restore search focus."
        );

        model.Select(selectedId);
        var minimum = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "minimum-150",
            480,
            520,
            1.5f
        );
        Assert.IsTrue(model.ShowEditor, "Selecting the current row did not reopen compact editor.");
        AssertPresent(minimum, body, true, "minimum-size body editor");
        AssertSemantic(composition, search, false, "minimum hidden collection");
        AssertSemantic(composition, body, true, "minimum body editor");
        AssertInsideViewport(
            minimum,
            480,
            520,
            title,
            url,
            body,
            Semantic(composition, "Back to collection", SemanticRole.Button),
            Semantic(composition, "Save now", SemanticRole.Button),
            Semantic(composition, "Archive", SemanticRole.Button),
            Semantic(composition, "Backup", SemanticRole.Button)
        );

        var returnedWide = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "wide-return",
            1180,
            760,
            1
        );
        Assert.AreEqual(NoteWorkspaceLayout.Wide, model.Layout);
        AssertPresent(returnedWide, search, true, "returned wide collection");
        AssertPresent(returnedWide, title, true, "returned wide editor");
        Assert.AreSame(titleSession, model.Title, "Responsive layout replaced the title session.");
        Assert.AreSame(bodySession, model.Body, "Responsive layout replaced the body session.");
        Assert.AreSame(
            searchSession,
            model.Search,
            "Responsive layout replaced the search session."
        );
        Assert.AreEqual(
            selectedId,
            model.Selected.Value!.Id,
            "Responsive layout replaced selection."
        );
        Assert.AreEqual(
            "Responsive review draft",
            model.Title.Text,
            "Responsive layout lost title draft."
        );
        Assert.AreEqual(0, model.Body.Anchor, "Responsive layout lost body selection anchor.");
        Assert.AreEqual(5, model.Body.Caret, "Responsive layout lost body selection caret.");
        scrollAnchor = Semantic(composition, scrollAnchorLabel, SemanticRole.ListItem);
        Assert.AreEqual(
            retainedScrollAnchorY,
            returnedWide
                .Boxes.Single(box => box.Identity.ElementId == scrollAnchor.Identity.ElementId)
                .Bounds.Y,
            0.01f,
            "Bidirectional resize reset list scroll."
        );
        Assert.AreEqual(
            capture.Identity.ElementId,
            Semantic(
                composition,
                "Capture a link or thought",
                SemanticRole.TextField
            ).Identity.ElementId,
            "Capture remounted during resize."
        );
        Assert.AreEqual(
            search.Identity.ElementId,
            Semantic(composition, "Search saved items", SemanticRole.TextField).Identity.ElementId,
            "Collection search remounted during resize."
        );
        Assert.AreEqual(
            title.Identity.ElementId,
            Semantic(composition, "Title", SemanticRole.TextField).Identity.ElementId,
            "Editor title remounted during resize."
        );

        model.Capture.Text = "capture focus review " + new string('c', 512);
        Assert.IsTrue(
            composition
                .Input.DispatchKey(new(KeyCommandKind.Down, Key.N, KeyModifiers.Control))
                .Handled,
            "Ctrl+N was not handled by the shell command scope."
        );
        var focusCaptureScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-capture",
            1180,
            760,
            1,
            export: false
        );
        AssertInsideViewport(focusCaptureScene, 1180, 760, capture);
        Assert.AreEqual(
            capture.Identity.ElementId,
            composition.Input.FocusedElement?.ElementId,
            "Ctrl+N did not focus capture."
        );
        Assert.AreEqual(
            0,
            model.Capture.Anchor,
            "Ctrl+N did not select capture text from its start."
        );
        Assert.AreEqual(
            model.Capture.Text.Length,
            model.Capture.Caret,
            "Ctrl+N did not select all capture text."
        );

        var longBodyRecord = model.VisibleItems.Single(item =>
            item.Title.StartsWith("Long note", StringComparison.Ordinal)
        );
        model.Select(longBodyRecord.Id);
        fixture.Until(() => model.Selected.Value?.Id == longBodyRecord.Id && !model.IsBusy);
        var longBodyScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "medium-long-body",
            900,
            760,
            1
        );
        Assert.AreEqual(
            20_000,
            model.Body.Text.Length,
            "The long review body was not loaded intact."
        );
        AssertPresent(longBodyScene, body, true, "long-body editor");
        AssertInsideViewport(longBodyScene, 900, 760, title, url, body);
        AssertWraps(longBodyScene, body, "20,000-character body");

        var longUrlRecord = model.VisibleItems.Single(item =>
            item.Title.StartsWith("An unusually long article", StringComparison.Ordinal)
        );
        _ = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "compact-long-url-route",
            480,
            520,
            1.5f,
            export: false
        );
        model.Select(longUrlRecord.Id);
        fixture.Until(() => model.Selected.Value?.Id == longUrlRecord.Id && !model.IsBusy);
        var longUrlScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "minimum-long-url",
            480,
            520,
            1.5f
        );
        Assert.IsTrue(model.Url.Text.Length > 320, "The long review URL was not loaded intact.");
        Assert.AreEqual(
            longUrlRecord.Title,
            model.Title.Text,
            "The full long title was not loaded intact."
        );
        AssertPresent(longUrlScene, body, true, "long-URL minimum editor");
        AssertInsideViewport(
            longUrlScene,
            480,
            520,
            title,
            url,
            body,
            Semantic(composition, "Back to collection", SemanticRole.Button),
            Semantic(composition, "Save now", SemanticRole.Button),
            Semantic(composition, "Archive", SemanticRole.Button),
            Semantic(composition, "Backup", SemanticRole.Button)
        );
        AssertWraps(longUrlScene, body, "minimum-width note body");

        model.Search.Text = "Reading list " + new string('q', 320);
        model.Search.SetSelection(2, 4);
        _ = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-search-prepared",
            480,
            520,
            1.5f,
            export: false
        );
        Assert.IsTrue(
            composition
                .Input.DispatchKey(new(KeyCommandKind.Down, Key.F, KeyModifiers.Control))
                .Handled,
            "Ctrl+F was not handled by the shell command scope."
        );
        var focusSearchScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "focus-search",
            480,
            520,
            1.5f,
            export: false
        );
        AssertInsideViewport(focusSearchScene, 480, 520, search);
        Assert.IsTrue(model.ShowCollection, "Ctrl+F did not reveal the compact collection.");
        Assert.AreEqual(
            search.Identity.ElementId,
            composition.Input.FocusedElement?.ElementId,
            "Ctrl+F did not focus search."
        );
        Assert.AreEqual(
            0,
            model.Search.Anchor,
            "Ctrl+F did not select search text from its start."
        );
        Assert.AreEqual(
            model.Search.Text.Length,
            model.Search.Caret,
            "Ctrl+F did not select all search text."
        );

        model.Search.Text = "no review record has this exact phrase";
        fixture.Until(() => model.VisibleItems.Count == 0);
        _ = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "no-results-route",
            560,
            760,
            1,
            export: false
        );
        model.BackToCollection();
        _ = Scenario(fixture, composition, renderer, model, "no-results", 560, 760, 1);
        Assert.IsTrue(
            Descendants(composition.SemanticSnapshot())
                .Any(node => node.Name.StartsWith("No matches for", StringComparison.Ordinal)),
            "No-results state did not replace the virtualized list."
        );
        AssertSemantic(composition, title, false, "no-results hidden editor");
    }

    [TestMethod]
    public void StartupFailureKeepsErrorAndRetryInsideMinimumViewport()
    {
        const string errorMessage =
            "The notes database could not be opened because the local folder is temporarily unavailable. Retry after the folder is available again.";
        using var fixture = new Fixture(new IOException(errorMessage));
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.IsTrue(model.HasError, "Injected startup failure did not reach the workspace.");
        Assert.IsFalse(model.IsReady, "Failed startup incorrectly marked the workspace ready.");
        Assert.IsTrue(model.RetryCommand.IsEnabled, "Failed startup did not expose retry.");

        using var composition = new Composition(fixture.Graph, "light-notes-error-review");
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();
        var scene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "minimum-storage-error",
            480,
            520,
            1.5f
        );
        var heading = Semantic(composition, "Couldn't open your notes", SemanticRole.Text);
        var detail = Semantic(composition, errorMessage, SemanticRole.Text);
        var retry = Semantic(composition, "Retry", SemanticRole.Button);
        AssertInsideViewport(scene, 480, 520, heading, detail, retry);
        AssertWraps(scene, detail, "startup storage failure detail");
        var headingBounds = scene
            .Boxes.Single(box => box.Identity.ElementId == heading.Identity.ElementId)
            .Bounds;
        var detailBounds = scene
            .Boxes.Single(box => box.Identity.ElementId == detail.Identity.ElementId)
            .Bounds;
        var retryBounds = scene
            .Boxes.Single(box => box.Identity.ElementId == retry.Identity.ElementId)
            .Bounds;
        Assert.IsTrue(
            headingBounds.Y + headingBounds.Height <= detailBounds.Y + 1f
                && detailBounds.Y + detailBounds.Height <= retryBounds.Y + 1f,
            $"Minimum-size error content overlaps: heading={headingBounds}; detail={detailBounds}; retry={retryBounds}."
        );
        Assert.AreEqual(
            "Could not open notes",
            model.ErrorHeading,
            "Startup failure lost its operation-specific model heading."
        );
    }

    private static RetainedScene Scenario(
        Fixture fixture,
        Composition composition,
        SkiaSceneRenderer renderer,
        NoteWorkspace model,
        string name,
        int width,
        int height,
        float scale,
        bool export = true
    )
    {
        RetainedScene? scene = null;
        var accepted = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixture.Drain();
            composition.Flush();
            scene = SceneLayout.Project(composition, new(width, height, scale), renderer);
            accepted = composition.Input.SetScene(scene);
            if (accepted && attempt >= 1)
                break;
        }
        Assert.IsNotNull(scene);
        Assert.IsTrue(accepted, $"{name} scene did not settle after bounded focus retries.");
        Assert.AreEqual(
            width,
            model.Constraints.Current.Width,
            0.01f,
            $"{name} width was not published."
        );
        Assert.AreEqual(
            height,
            model.Constraints.Current.Height,
            0.01f,
            $"{name} height was not published."
        );

        using var bitmap = new SKBitmap(
            checked((int)MathF.Ceiling(width * scale)),
            checked((int)MathF.Ceiling(height * scale)),
            SKColorType.Rgba8888,
            SKAlphaType.Premul
        );
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            renderer.Render(scene, canvas);
        }
        Assert.IsTrue(
            Enumerable
                .Range(0, bitmap.Height)
                .Any(y =>
                    Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).Alpha != 0)
                ),
            $"{name} rendered no pixels."
        );
        if (
            export
            && Environment.GetEnvironmentVariable("LIGHT_NOTES_REVIEW_ARTIFACTS")
                is { Length: > 0 } directory
        )
        {
            Directory.CreateDirectory(directory);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            data.SaveTo(stream);
        }
        return scene;
    }

    private static SemanticSnapshot Semantic(
        Composition composition,
        string name,
        SemanticRole role
    ) =>
        Descendants(composition.SemanticSnapshot())
            .Single(node => node.Name == name && node.Role == role);

    private static SemanticSnapshot SemanticStartingWith(
        Composition composition,
        string name,
        SemanticRole role
    ) =>
        Descendants(composition.SemanticSnapshot())
            .Single(node =>
                node.Name.StartsWith(name, StringComparison.Ordinal) && node.Role == role
            );

    private static SemanticSnapshot SemanticText(Composition composition, string name) =>
        Descendants(composition.SemanticSnapshot())
            .Single(node => node.Name == name && node.Text is not null);

    private static void AssertPresent(
        RetainedScene scene,
        SemanticSnapshot element,
        bool expected,
        string message
    ) =>
        Assert.AreEqual(
            expected,
            scene.Boxes.Any(box =>
                box.Identity.ElementId == element.Identity.ElementId
                && box.Bounds.Width > 0
                && box.Bounds.Height > 0
            ),
            message
        );

    private static void AssertSemantic(
        Composition composition,
        SemanticSnapshot element,
        bool expected,
        string message
    )
    {
        var actual = Descendants(composition.SemanticSnapshot())
            .Any(node => node.Identity.ElementId == element.Identity.ElementId);
        Assert.AreEqual(expected, actual, message);
    }

    private static void AssertBounds(
        RetainedScene scene,
        SemanticSnapshot element,
        float? expectedWidth = null,
        float? maximumX = null,
        string? message = null
    )
    {
        var bounds = scene
            .Boxes.Single(box => box.Identity.ElementId == element.Identity.ElementId)
            .Bounds;
        if (expectedWidth is { } width)
            Assert.AreEqual(width, bounds.Width, 1f, message);
        if (maximumX is { } right)
            Assert.IsTrue(bounds.X + bounds.Width <= right + 1f, message);
    }

    private static void AssertWraps(RetainedScene scene, SemanticSnapshot element, string message)
    {
        var box = scene.Boxes.Single(box => box.Identity.ElementId == element.Identity.ElementId);
        Assert.IsNotNull(box.Text, $"{message} has no shaped text.");
        Assert.IsTrue(box.Text.Lines.Count > 1, $"{message} did not wrap into multiple lines.");
    }

    private static void AssertNearBottom(
        RetainedScene scene,
        SemanticSnapshot element,
        float height,
        string message
    )
    {
        var bounds = scene
            .Boxes.Single(box => box.Identity.ElementId == element.Identity.ElementId)
            .Bounds;
        Assert.IsTrue(bounds.Height > 0, $"{message} has empty geometry.");
        Assert.IsTrue(
            bounds.Y + bounds.Height >= height - 80,
            $"{message} leaves excessive unused space."
        );
    }

    private static void AssertInsideViewport(
        RetainedScene scene,
        float width,
        float height,
        params SemanticSnapshot[] elements
    )
    {
        foreach (var element in elements)
        {
            var bounds = scene
                .Boxes.Single(box => box.Identity.ElementId == element.Identity.ElementId)
                .Bounds;
            Assert.IsTrue(
                bounds.Width > 0 && bounds.Height > 0,
                $"{element.Name} has empty minimum-size geometry."
            );
            Assert.IsTrue(
                bounds.X >= 0 && bounds.Y >= 0,
                $"{element.Name} begins outside the minimum viewport."
            );
            Assert.IsTrue(
                bounds.X + bounds.Width <= width + 1f,
                $"{element.Name} extends past the minimum viewport width."
            );
            Assert.IsTrue(
                bounds.Y + bounds.Height <= height + 1f,
                $"{element.Name} extends past the minimum viewport height."
            );
        }
    }

    private static IEnumerable<SemanticSnapshot> Descendants(SemanticSnapshot? root) =>
        root is null ? [] : new[] { root }.Concat(root.Children.SelectMany(Descendants));

    // Offscreen layout scenarios hold time still; autosave timing is covered by WorkspaceTests.
    private sealed class PausedDebounce : IDebounceScheduler
    {
        public void Restart(TimeSpan delay, Action callback) { }

        public void Cancel() { }

        public void Dispose() { }
    }

    private sealed class NoExternalLinks : IExternalLinkOpener
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offscreen review must not open a browser.");
    }

    private sealed class Fixture : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _prior = Current;
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "light-notes-shell-" + Guid.NewGuid().ToString("N")
        );

        internal Fixture(Exception? startupFailure = null)
        {
            Directory.CreateDirectory(_directory);
            ReviewSamples.SeedAsync(DatabasePath).GetAwaiter().GetResult();
            SetSynchronizationContext(this);
            Graph = new ReactiveGraph();
            Scope = Graph.CreateScope("shell-review-model");
            Model = startupFailure is null
                ? new NoteWorkspace(
                    Scope,
                    DatabasePath,
                    new NoExternalLinks(),
                    new PausedDebounce()
                )
                : new NoteWorkspace(
                    Scope,
                    DatabasePath,
                    new NoExternalLinks(),
                    new PausedDebounce(),
                    _ => Task.FromException<INoteWorkspaceStorage>(startupFailure)
                );
        }

        internal string DatabasePath => Path.Combine(_directory, "notes.db");
        internal ReactiveGraph Graph { get; }
        internal ReactiveScope Scope { get; }
        internal NoteWorkspace Model { get; }

        public override void Post(SendOrPostCallback callback, object? state) =>
            _queue.Enqueue(() => callback(state));

        internal void Pump(Task task)
        {
            Until(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        internal void Until(Func<bool> complete)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!complete())
            {
                Drain();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Shell review work did not complete.");
                Thread.Sleep(1);
            }
            Drain();
        }

        internal void Drain()
        {
            while (_queue.TryDequeue(out var callback))
                callback();
            Graph.Drain();
        }

        public void Dispose()
        {
            try
            {
                Pump(Model.DisposeAsync().AsTask());
                Scope.Dispose();
            }
            finally
            {
                SetSynchronizationContext(_prior);
            }
            if (!Directory.Exists(_directory))
                return;
            var full = Path.GetFullPath(_directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (
                !full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full)
                    .StartsWith("light-notes-shell-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException(
                    "Refusing cleanup outside the shell review directory."
                );
            Directory.Delete(full, true);
        }
    }
}
