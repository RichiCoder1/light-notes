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
}
