namespace LightNotes;

/// <summary>The three stable presentation widths used by the Light Notes shell.</summary>
public enum NoteWorkspaceLayout
{
    /// <summary>The compact single-pane route used below the medium breakpoint.</summary>
    Compact,

    /// <summary>The medium two-pane route used between the compact and wide breakpoints.</summary>
    Medium,

    /// <summary>The wide two-pane route used at 1060 logical pixels and above.</summary>
    Wide,
}

/// <summary>The route retained by the compact Light Notes shell.</summary>
public enum NoteWorkspaceRoute
{
    /// <summary>The collection and search route.</summary>
    Collection,

    /// <summary>The selected note editor route.</summary>
    Editor,
}
