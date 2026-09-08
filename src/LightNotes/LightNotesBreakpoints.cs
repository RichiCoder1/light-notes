using Lucent.Core;

namespace LightNotes;

internal static class LightNotesBreakpoints
{
    public static readonly Breakpoint Medium = new("medium", 840);
    public static readonly Breakpoint Wide = new("wide", 1060);
    public static readonly BreakpointSet Set = BreakpointSet.Create(Medium, Wide);
}
