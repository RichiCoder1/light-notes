using Lucent.Core;

namespace LightNotes;

[LucentRouteModule(RouteFallbackPolicy.Reject)]
internal static partial class LightNotesRoutes { }

[LucentRoute(typeof(LightNotesRoutes), "/", Id = "workspace")]
internal readonly record struct WorkspaceRoute();

[LucentRoute(typeof(LightNotesRoutes), "/inbox", Id = "inbox", Parent = typeof(WorkspaceRoute))]
internal readonly record struct InboxRoute();

[LucentRoute(
    typeof(LightNotesRoutes),
    "/inbox/{id}",
    Id = "inbox-note",
    Parent = typeof(InboxRoute)
)]
internal readonly record struct InboxNoteRoute(Guid Id);

[LucentRoute(typeof(LightNotesRoutes), "/archive", Id = "archive", Parent = typeof(WorkspaceRoute))]
internal readonly record struct ArchiveRoute();

[LucentRoute(
    typeof(LightNotesRoutes),
    "/archive/{id}",
    Id = "archive-note",
    Parent = typeof(ArchiveRoute)
)]
internal readonly record struct ArchiveNoteRoute(Guid Id);

internal static class LightNotesRouting
{
    internal static RouteBundle Bundle { get; } =
        RouteBundle.Create([LightNotesRoutes.Module], Destination);

    internal static RouteTable Table => Bundle.Table;

    internal static ComponentRecipe Root(NoteWorkspace workspace) =>
        Lucent.Core.Components.Router(
            [
                Lucent.Core.Components.RouterOutlet(
                    options: new RouteOutletOptions(
                        workspace.PrepareNavigation,
                        workspace.NavigationInteraction
                    )
                ),
            ],
            Bundle,
            session: workspace.Navigation
        );

    internal static ComponentRecipe Child() => Lucent.Core.Components.RouterOutlet();

    internal static RouteDestination Destination(RouteLevelDescriptor level) =>
        level.Id.Value switch
        {
            "workspace" => new(typeof(WorkspaceRoute), Components.RoutedWorkspaceShell()),
            "inbox" => new(typeof(InboxRoute), Components.InboxRouteState()),
            "inbox-note" => new(typeof(InboxNoteRoute), Components.InboxNoteRouteState()),
            "archive" => new(typeof(ArchiveRoute), Components.ArchiveRouteState()),
            "archive-note" => new(typeof(ArchiveNoteRoute), Components.ArchiveNoteRouteState()),
            _ => throw new InvalidOperationException("Unknown Light Notes route."),
        };
}
