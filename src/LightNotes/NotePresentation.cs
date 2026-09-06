using LightNotes.Storage;

namespace LightNotes;

/// <summary>Pure presentation text for note rows and collection states.</summary>
internal static class NotePresentation
{
    internal static string RowLabel(NoteRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var title = SingleLine(item.Title);
        var kind = item.Kind == NoteKind.Link ? "LINK" : "NOTE";
        var edited = item
            .UpdatedAt.ToLocalTime()
            .ToString("MMM d · h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        return $"{title}\n{kind} · edited {edited}";
    }

    internal static string CollectionCount(NoteWorkspace model) =>
        model.IsBusy ? "Updating…"
        : model.VisibleItems.Count == 1 ? "1 item"
        : $"{model.VisibleItems.Count} items";

    internal static string EmptyHeading(NoteWorkspace model) =>
        model.Search.Text.Length != 0 ? $"No matches for “{SingleLine(model.Search.Text)}”"
        : model.ShowArchived.Value ? "Nothing archived"
        : "Your inbox is clear";

    internal static string EmptyGuidance(NoteWorkspace model) =>
        model.Search.Text.Length != 0 ? "Try another phrase or clear the current search."
        : model.ShowArchived.Value ? "Notes you archive will stay here until you restore them."
        : "Capture a link or thought above. It will be ready to edit here.";

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
