using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class ShellPresentationTests
{
    [TestMethod]
    public void NoteMenuArchivesClickedRowWithoutReplacingActiveDraft()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        var active = model.Selected.Value!.Id;
        var target = model.VisibleItems.First(note => note.Id != active);
        model.Url.Text = "unfinished-address";

        using var composition = new Composition(fixture.Graph, "note-menu-review");
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();
        var scene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "note-menu",
            1180,
            760,
            1,
            export: false
        );
        var row = Semantic(composition, NotePresentation.RowLabel(target), SemanticRole.ListItem);
        var bounds = Bounds(scene, row);
        ContextMenuRequest? request = null;
        composition.Input.ContextMenuRequested += value => request = value;
        composition.Input.DispatchPointer(
            new(PointerCommandKind.Down, 1, bounds.X + 8, bounds.Y + 8, PointerButton.Secondary)
        );
        _ = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "note-menu-down",
            1180,
            760,
            1,
            export: false
        );
        composition.Input.DispatchPointer(
            new(PointerCommandKind.Up, 1, bounds.X + 8, bounds.Y + 8)
        );
        Assert.IsNotNull(request);
        Assert.AreEqual(active, model.Selected.Value!.Id);
        var openScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "note-menu-target",
            1180,
            760,
            1,
            export: false
        );
        row = Semantic(composition, NotePresentation.RowLabel(target), SemanticRole.ListItem);
        Assert.IsFalse(row.Selected, "Menu targeting incorrectly committed note selection.");
        Assert.IsTrue(
            SceneNodes(openScene.Nodes)
                .Any(node =>
                    node.Identity.Kind == SceneNodeKind.FocusRing
                    && node.Identity.Element.ElementId == row.Identity.ElementId
                ),
            "The .lui menu-open state did not show a distinct target outline."
        );
        using (request)
        {
            var popup = request.CreateComposition();
            Assert.IsTrue(
                popup.Input.SetScene(SceneLayout.Project(popup, new(260, 200, 1), renderer))
            );
            var action = Descendants(popup.SemanticSnapshot())
                .Single(node => node.Role == SemanticRole.MenuItem && node.Name == "Archive");
            Assert.AreEqual(
                SemanticCommandResult.Applied,
                popup.ExecuteSemanticCommand(action.Identity, new(SemanticCommandKind.Invoke))
            );
            Assert.IsTrue(request.IsDismissed);
        }
        // Closing the popup must leave the accepted application-owned command running.
        fixture.Until(() => model.VisibleItems.All(note => note.Id != target.Id));
        Assert.AreEqual(active, model.Selected.Value!.Id);
        Assert.AreEqual("unfinished-address", model.Url.Text);
    }
}
