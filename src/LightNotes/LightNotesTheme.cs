using Lucent.Core;

namespace LightNotes;

/// <summary>Presentation tokens for the quiet, continuous Light Notes working surface.</summary>
internal static class LightNotesTheme
{
    /// <summary>Creates the Light Notes theme from Lucent's accessible light control defaults.</summary>
    internal static Theme Create() => ControlThemes.Light;

    internal static readonly Token<Brush> Canvas = new(
        "light-notes-canvas",
        Color.Parse("#F7F5F0")
    );
    internal static readonly Token<Brush> Surface = new(
        "light-notes-surface",
        Color.Parse("#FEFDFC")
    );
    internal static readonly Token<Brush> Field = new("light-notes-field", Color.Parse("#FBFAF6"));
    internal static readonly Token<Brush> Navigation = new(
        "light-notes-navigation",
        Color.Parse("#26334A")
    );
    internal static readonly Token<Brush> NavigationSelected = new(
        "light-notes-navigation-selected",
        Color.Parse("#344866")
    );
    internal static readonly Token<Brush> NavigationHover = new(
        "light-notes-navigation-hover",
        Color.Parse("#2E405B")
    );
    internal static readonly Token<Brush> NavigationPressed = new(
        "light-notes-navigation-pressed",
        Color.Parse("#405875")
    );
    internal static readonly Token<Brush> Accent = new(
        "light-notes-accent",
        Color.Parse("#315F7B")
    );
    internal static readonly Token<Brush> AccentHover = new(
        "light-notes-accent-hover",
        Color.Parse("#3B7390")
    );
    internal static readonly Token<Brush> AccentPressed = new(
        "light-notes-accent-pressed",
        Color.Parse("#254B61")
    );
    internal static readonly Token<Color> AccentInk = new(
        "light-notes-accent-ink",
        Color.Parse("#315F7B")
    );
    internal static readonly Token<Brush> Selection = new(
        "light-notes-selection",
        Color.Parse("#DFEAF0")
    );
    internal static readonly Token<Brush> Hover = new("light-notes-hover", Color.Parse("#EDF1F2"));
    internal static readonly Token<Brush> ScrollbarTrack = new(
        "light-notes-scrollbar-track",
        Color.Parse("#EDE9E1")
    );
    internal static readonly Token<Brush> ScrollbarThumb = new(
        "light-notes-scrollbar-thumb",
        Color.Parse("#84796D")
    );
    internal static readonly Token<Brush> ScrollbarThumbHover = new(
        "light-notes-scrollbar-thumb-hover",
        Color.Parse("#6F6357")
    );
    internal static readonly Token<Brush> ScrollbarThumbPressed = new(
        "light-notes-scrollbar-thumb-pressed",
        Color.Parse("#594E44")
    );
    internal static readonly Token<Brush> SurfacePressed = new(
        "light-notes-surface-pressed",
        Color.Parse("#E3E8E8")
    );
    internal static readonly Token<Color> Ink = new("light-notes-ink", Color.Parse("#202A34"));
    internal static readonly Token<Color> MutedInk = new(
        "light-notes-muted-ink",
        Color.Parse("#66727A")
    );
    internal static readonly Token<Color> InverseInk = new(
        "light-notes-inverse-ink",
        Color.Parse("#F8FAFC")
    );
    internal static readonly Token<Brush> Divider = new(
        "light-notes-divider",
        Color.Parse("#D9D6CF")
    );
    internal static readonly Token<Brush> FieldBorder = new(
        "light-notes-field-border",
        Color.Parse("#B8C0C5")
    );
    internal static readonly Token<Brush> Focus = new("light-notes-focus", Color.Parse("#276C94"));
    internal static readonly Token<Brush> ErrorSurface = new(
        "light-notes-error-surface",
        Color.Parse("#FCEBEC")
    );
    internal static readonly Token<Brush> ErrorHover = new(
        "light-notes-error-hover",
        Color.Parse("#F6DDE0")
    );
    internal static readonly Token<Brush> ErrorPressed = new(
        "light-notes-error-pressed",
        Color.Parse("#F2CCD1")
    );
    internal static readonly Token<Color> ErrorInk = new(
        "light-notes-error-ink",
        Color.Parse("#8B2635")
    );
    internal static readonly Token<Border> DividerBottom = new(
        "light-notes-divider-bottom",
        Border.Hairline(Color.Parse("#D9D6CF"), BorderSides.Bottom)
    );
    internal static readonly Token<Border> DividerRight = new(
        "light-notes-divider-right",
        Border.Hairline(Color.Parse("#D9D6CF"), BorderSides.Right)
    );
    internal static readonly Token<Border> RowSelectionOutline = new(
        "light-notes-row-selection-outline",
        Border.Edges(Color.Parse("#315F7B"), new Insets(3, 0, 0, 0))
    );
    internal static readonly Token<Border> FieldOutline = new(
        "light-notes-field-outline",
        Border.Uniform(Color.Parse("#B8C0C5"), 1)
    );
    internal static readonly Token<Border> ErrorOutline = new(
        "light-notes-error-outline",
        Border.Uniform(Color.Parse("#B24A58"), 1)
    );
    internal static readonly Token<FocusRing> KeyboardFocus = new(
        "light-notes-keyboard-focus",
        FocusRing.Inset(Color.Parse("#276C94"), 2)
    );
    internal static readonly Token<FocusRing> KeyboardFocusOnDark = new(
        "light-notes-keyboard-focus-on-dark",
        FocusRing.Inset(Color.Parse("#F8FAFC"), 2)
    );
}
