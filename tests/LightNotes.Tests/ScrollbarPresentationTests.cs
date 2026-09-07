using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class ShellPresentationTests
{
    [TestMethod]
    public void ScrollbarStatesUseWarmPaletteAndRetainViewportGeometry()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        using var composition = new Composition(fixture.Graph, "light-notes-scrollbar-review");
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();

        var defaultScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "interaction-scrollbar-default",
            1180,
            760,
            1
        );
        var notesList = Semantic(composition, "Saved notes", SemanticRole.List);
        var listInput = defaultScene.Input.Single(item =>
            item.Identity.ElementId == notesList.Identity.ElementId
        );
        var listViewport =
            listInput.Parent
            ?? throw new InvalidOperationException(
                "The saved-notes region did not retain its scroll viewport parent."
            );
        var defaultBar = defaultScene.ScrollBars.Single(bar => bar.Viewport == listViewport);
        Assert.AreEqual(
            10f,
            defaultBar.Track.Width,
            0.01f,
            "Light Notes scrollbar thickness changed."
        );
        Assert.AreEqual(
            Color.Parse("#EDE9E1"),
            ScrollbarPaintColor(defaultScene, defaultBar, SceneNodeKind.ScrollBarTrack),
            "The default scrollbar track lost the warm app palette."
        );
        Assert.AreEqual(
            Color.Parse("#84796D"),
            ScrollbarPaintColor(defaultScene, defaultBar, SceneNodeKind.ScrollBarThumb),
            "The default scrollbar thumb lost the warm app palette."
        );

        var pointer = 91;
        var thumbX = defaultBar.Thumb.X + defaultBar.Thumb.Width / 2;
        var thumbY = defaultBar.Thumb.Y + defaultBar.Thumb.Height / 2;
        Assert.AreEqual(
            InputDispatchStatus.Delivered,
            composition
                .Input.DispatchPointer(new(PointerCommandKind.Move, pointer, thumbX, thumbY))
                .Status,
            "The collection scrollbar did not receive hover input."
        );
        var hoveredScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "interaction-scrollbar-hover",
            1180,
            760,
            1
        );
        var hoveredBar = hoveredScene.ScrollBars.Single(bar => bar.Viewport == listViewport);
        Assert.AreEqual(
            Color.Parse("#6F6357"),
            ScrollbarPaintColor(hoveredScene, hoveredBar, SceneNodeKind.ScrollBarThumb),
            "The collection scrollbar did not expose its warm hover surface."
        );

        Assert.IsTrue(
            composition
                .Input.DispatchPointer(
                    new(
                        PointerCommandKind.Down,
                        pointer,
                        hoveredBar.Thumb.X + hoveredBar.Thumb.Width / 2,
                        hoveredBar.Thumb.Y + hoveredBar.Thumb.Height / 2,
                        PointerButton.Primary
                    )
                )
                .Handled,
            "The collection scrollbar did not accept a primary press."
        );
        var pressedScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "interaction-scrollbar-pressed",
            1180,
            760,
            1
        );
        var pressedBar = pressedScene.ScrollBars.Single(bar => bar.Viewport == listViewport);
        Assert.AreEqual(
            Color.Parse("#594E44"),
            ScrollbarPaintColor(pressedScene, pressedBar, SceneNodeKind.ScrollBarThumb),
            "The collection scrollbar did not expose its warm pressed surface."
        );
        Assert.AreEqual(
            defaultBar.Track,
            pressedBar.Track,
            "Scrollbar interaction changed the retained track geometry."
        );
        composition.Input.DispatchPointer(
            new(PointerCommandKind.Cancel, pointer, pressedBar.Thumb.X, pressedBar.Thumb.Y)
        );
    }

    private static Color ScrollbarPaintColor(
        RetainedScene scene,
        RetainedScrollBar bar,
        SceneNodeKind kind
    )
    {
        var node = SceneNodes(scene.Nodes)
            .OfType<PaintSceneNode>()
            .Single(candidate =>
                candidate.Identity.Element == bar.Viewport && candidate.Identity.Kind == kind
            );
        Assert.IsTrue(node.Brush.Color is { }, $"Scrollbar {kind} did not resolve a solid brush.");
        return node.Brush.Color!.Value;
    }
}
