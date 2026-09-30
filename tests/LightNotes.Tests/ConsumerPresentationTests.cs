using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class WorkspaceTests
{
    [TestMethod]
    public void CommandButtonsAndMultilineFieldObserveWorkspaceWithoutInput()
    {
        var record = ControlledStorage.Record("Kept title", "Kept body");
        using var fixture = new Fixture(new CollectionStorage([record]));
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        model.Select(record.Id);
        fixture.Until(() => model.ShowEditor && !model.IsBusy);
        model.TitleFocus.Cancel();
        var bodySession = model.Body;
        var titleSession = model.Title;
        var titleFocus = model.TitleFocus;

        using (var composition = new Composition(fixture.Graph, "notes-consumer-contract"))
        using (var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create()))
        {
            composition.ConfigureImages(new ImageCache(new SkiaImagePreparer()));
            composition.Mount(
                composition.Root,
                theme,
                global::LightNotes.Components.CaptureBar(model)
            );
            composition.Mount(
                composition.Root,
                theme,
                global::LightNotes.Components.EditorPane(model)
            );
            composition.Flush();
            var before = ConsumerSemantics(composition.SemanticSnapshot()!).ToArray();
            var body = before.Single(node => node.Name == "Notes" && node.Text is not null);
            var label = before.Single(node =>
                node.Name == "Notes" && node.Role == SemanticRole.Text
            );
            var title = before.Single(node => node.Name == "Title" && node.Text is not null);
            Assert.AreEqual(label.Identity.ElementId, body.Relationships!.Label!.Value.ElementId);
            Assert.AreEqual("Kept body", body.Text!.Text);
            Assert.IsTrue(body.Enabled);
            Assert.IsFalse(
                before
                    .Single(node => node.Role == SemanticRole.Button && node.Name == "Add")
                    .Enabled
            );
            Assert.IsFalse(
                before
                    .Single(node => node.Role == SemanticRole.Button && node.Name == "Save now")
                    .Enabled
            );

            var elements = ConsumerElements(composition.Root).ToArray();
            var bodyElement = elements.Single(element => element.Id == body.Identity.ElementId);
            var labelElement = elements.Single(element => element.Id == label.Identity.ElementId);
            Assert.AreEqual(1f, bodyElement.Resolve(LayoutProperties.MainGrow).Value);
            Assert.AreEqual(0f, bodyElement.Resolve(LayoutProperties.MinHeight).Value);
            Assert.AreEqual(12f, labelElement.Resolve(TypographyProperties.FontSize).Value);
            Assert.AreEqual(
                Color.Parse("#202A34"),
                bodyElement.Resolve(TypographyProperties.TextColor).Value
            );
            Assert.AreEqual(
                Color.Parse("#5C6870"),
                bodyElement.Resolve(TypographyProperties.PlaceholderTextColor).Value
            );

            model.Capture.Text = "Ready to capture";
            model.Body.Text = "Changed draft\nSecond line";
            fixture.Drain();
            composition.Flush();
            var edited = ConsumerSemantics(composition.SemanticSnapshot()!).ToArray();
            var editedBody = edited.Single(node => node.Name == "Notes" && node.Text is not null);
            Assert.AreEqual(body.Identity.ElementId, editedBody.Identity.ElementId);
            Assert.AreEqual(
                label.Identity.ElementId,
                editedBody.Relationships!.Label!.Value.ElementId
            );
            Assert.AreEqual(
                title.Identity.ElementId,
                edited
                    .Single(node => node.Name == "Title" && node.Text is not null)
                    .Identity.ElementId
            );
            Assert.AreEqual("Changed draft\nSecond line", editedBody.Text!.Text);
            Assert.IsTrue(
                edited
                    .Single(node => node.Role == SemanticRole.Button && node.Name == "Add")
                    .Enabled
            );
            Assert.IsTrue(
                edited
                    .Single(node => node.Role == SemanticRole.Button && node.Name == "Save now")
                    .Enabled
            );

            var close = model.PrepareCloseAsync().AsTask();
            fixture.Pump(close);
            Assert.IsTrue(close.Result);
            composition.Flush();
            var closing = ConsumerSemantics(composition.SemanticSnapshot()!).ToArray();
            Assert.IsFalse(
                closing.Single(node => node.Name == "Notes" && node.Text is not null).Enabled
            );
            Assert.IsFalse(
                closing.Single(node => node.Name == "Title" && node.Text is not null).Enabled
            );
            Assert.IsFalse(
                closing
                    .Single(node => node.Role == SemanticRole.Button && node.Name == "Add")
                    .Enabled
            );
        }

        Assert.AreSame(bodySession, model.Body);
        Assert.AreSame(titleSession, model.Title);
        Assert.AreSame(titleFocus, model.TitleFocus);
        model.Body.Text = "Still owned by the workspace";
        Assert.AreEqual("Still owned by the workspace", model.Body.Text);
    }

    [TestMethod]
    public void SmallMetadataRetainsTextContrastAcrossRowSurfaces()
    {
        var foreground = LightNotesTheme.MutedInk.Fallback;
        foreach (
            var surface in new[]
            {
                LightNotesTheme.Surface,
                LightNotesTheme.Selection,
                LightNotesTheme.Hover,
                LightNotesTheme.SurfacePressed,
                LightNotesTheme.Canvas,
                LightNotesTheme.Field,
            }
        )
        {
            var background = surface.Fallback.Color!.Value;
            var ratio =
                (ConsumerLuminance(background) + 0.05) / (ConsumerLuminance(foreground) + 0.05);
            Assert.IsTrue(
                ratio >= 4.5,
                $"Small metadata contrast on {surface.Name} was {ratio:F3}:1."
            );
        }
    }

    private static IEnumerable<Element> ConsumerElements(Element element)
    {
        yield return element;
        foreach (var child in element.Children)
        foreach (var descendant in ConsumerElements(child))
            yield return descendant;
    }

    private static IEnumerable<SemanticSnapshot> ConsumerSemantics(SemanticSnapshot node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in ConsumerSemantics(child))
            yield return descendant;
    }

    private static double ConsumerLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
