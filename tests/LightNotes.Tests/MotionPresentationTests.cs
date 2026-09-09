using LightNotes.Storage;
using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class ShellPresentationTests
{
    [TestMethod]
    public void LuiRowAndBackHoverMotionKeepsWorkspaceStateAndImmediatePresses()
    {
        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());

        using var composition = new Composition(fixture.Graph, "notes-motion-presentation");
        composition.ConfigureImages(new ImageCache(new SkiaImagePreparer()));
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();

        var selectedId = model.Selected.Value!.Id;
        model.Select(selectedId);
        fixture.Drain();
        var titleSession = model.Title;
        var bodySession = model.Body;
        model.Title.Text = "Motion-retained title draft";
        model.Body.Text = "Motion-retained body draft";
        model.Body.SetSelection(2, 8);

        var now = TimeSpan.Zero;
        using var baseline = MotionFrame(
            fixture,
            composition,
            renderer,
            previous: null,
            new(1180, 760, 1),
            now
        );
        var selectedRow = Semantic(
            composition,
            NotePresentation.RowLabel(model.Selected.Value!),
            SemanticRole.ListItem
        );
        Assert.AreEqual(
            Color.Parse("#DFEAF0"),
            BackgroundColor(baseline, selectedRow),
            "The selected row policy was not immediate at the acknowledged baseline."
        );
        var hoveredItem = model.VisibleItems.First(item => item.Id != selectedId);
        var hoveredRow = Semantic(
            composition,
            NotePresentation.RowLabel(hoveredItem),
            SemanticRole.ListItem
        );
        var hoveredBounds = Bounds(baseline, hoveredRow);
        const int rowPointer = 901;
        composition.Input.DispatchPointer(
            new(
                PointerCommandKind.Move,
                rowPointer,
                hoveredBounds.X + hoveredBounds.Width / 2,
                hoveredBounds.Y + hoveredBounds.Height / 2
            )
        );
        using var rowTarget = MotionFrame(
            fixture,
            composition,
            renderer,
            baseline,
            baseline.Viewport,
            now
        );
        now += TimeSpan.FromMilliseconds(60);
        using var rowMiddle = MotionFrame(
            fixture,
            composition,
            renderer,
            rowTarget,
            rowTarget.Viewport,
            now
        );
        Assert.IsTrue(rowMiddle.IsPaintOnly, "Row hover did not use sample-only paint replay.");
        Assert.AreNotEqual(
            BackgroundColor(rowTarget, hoveredRow),
            BackgroundColor(rowMiddle, hoveredRow),
            "Row hover did not advance after an explicit presentation sample."
        );
        AssertWorkspaceState(model, selectedId, titleSession, bodySession);

        composition.Input.DispatchPointer(
            new(
                PointerCommandKind.Down,
                rowPointer,
                hoveredBounds.X + hoveredBounds.Width / 2,
                hoveredBounds.Y + hoveredBounds.Height / 2,
                PointerButton.Primary
            )
        );
        using var rowPressed = MotionFrame(
            fixture,
            composition,
            renderer,
            rowMiddle,
            rowMiddle.Viewport,
            now
        );
        Assert.AreEqual(
            Color.Parse("#E3E8E8"),
            BackgroundColor(rowPressed, hoveredRow),
            "The explicit row press policy was animated instead of applying immediately."
        );
        AssertWorkspaceState(model, selectedId, titleSession, bodySession);
        composition.Input.DispatchPointer(
            new(PointerCommandKind.Cancel, rowPointer, hoveredBounds.X, hoveredBounds.Y)
        );

        using var compact = MotionFrame(
            fixture,
            composition,
            renderer,
            rowPressed,
            new(560, 760, 1),
            now
        );
        var back = Semantic(composition, "Back to collection", SemanticRole.Button);
        var backBounds = Bounds(compact, back);
        const int backPointer = 902;
        composition.Input.DispatchPointer(
            new(
                PointerCommandKind.Move,
                backPointer,
                backBounds.X + backBounds.Width / 2,
                backBounds.Y + backBounds.Height / 2
            )
        );
        using var backTarget = MotionFrame(
            fixture,
            composition,
            renderer,
            compact,
            compact.Viewport,
            now
        );
        now += TimeSpan.FromMilliseconds(60);
        using var backMiddle = MotionFrame(
            fixture,
            composition,
            renderer,
            backTarget,
            backTarget.Viewport,
            now
        );
        Assert.IsTrue(backMiddle.IsPaintOnly, "Back hover did not use sample-only paint replay.");
        Assert.AreNotEqual(
            BackgroundColor(backTarget, back),
            BackgroundColor(backMiddle, back),
            "Back hover did not advance after an explicit presentation sample."
        );
        AssertWorkspaceState(model, selectedId, titleSession, bodySession);

        composition.Input.DispatchPointer(
            new(
                PointerCommandKind.Down,
                backPointer,
                backBounds.X + backBounds.Width / 2,
                backBounds.Y + backBounds.Height / 2,
                PointerButton.Primary
            )
        );
        using var backPressed = MotionFrame(
            fixture,
            composition,
            renderer,
            backMiddle,
            backMiddle.Viewport,
            now
        );
        Assert.AreEqual(
            Color.Parse("#E3E8E8"),
            BackgroundColor(backPressed, back),
            "The explicit back press policy was animated instead of applying immediately."
        );
        AssertWorkspaceState(model, selectedId, titleSession, bodySession);
    }

    private static RetainedScene MotionFrame(
        Fixture fixture,
        Composition composition,
        SkiaSceneRenderer renderer,
        RetainedScene? previous,
        LayoutViewport viewport,
        TimeSpan timestamp
    )
    {
        fixture.Drain();
        composition.Flush();
        composition.SamplePresentation(timestamp);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var candidate = SceneLayout.ProjectFrame(composition, viewport, renderer, previous);
            if (composition.Input.SetScene(candidate))
            {
                Assert.IsTrue(
                    composition.TryAcknowledgePresentation(candidate.Generation),
                    "The installed Light Notes motion frame was not acknowledged."
                );
                return candidate;
            }
            candidate.Dispose();
            fixture.Drain();
            composition.Flush();
        }
        throw new InvalidOperationException("Light Notes motion projection did not settle.");
    }

    private static void AssertWorkspaceState(
        NoteWorkspace model,
        Guid selectedId,
        EditorSession titleSession,
        EditorSession bodySession
    )
    {
        Assert.AreSame(titleSession, model.Title);
        Assert.AreSame(bodySession, model.Body);
        Assert.AreEqual(selectedId, model.Selected.Value!.Id);
        Assert.AreEqual("Motion-retained title draft", model.Title.Text);
        Assert.AreEqual("Motion-retained body draft", model.Body.Text);
        Assert.AreEqual(2, model.Body.Anchor);
        Assert.AreEqual(8, model.Body.Caret);
    }
}
