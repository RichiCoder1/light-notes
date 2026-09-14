using Lucent.Core;

namespace LightNotes;

[LucentRouteModule(RouteFallbackPolicy.Reject)]
internal static partial class LightNotesRoutes { }

[LucentRoute(typeof(LightNotesRoutes), "/", Id = "workspace")]
internal readonly record struct WorkspaceRoute();

[LucentRoute(typeof(LightNotesRoutes), "/inbox", Id = "inbox", Parent = typeof(WorkspaceRoute))]
internal readonly record struct InboxRoute();

[LucentRoute(typeof(LightNotesRoutes), "/inbox/{id}", Id = "inbox-note", Parent = typeof(InboxRoute))]
internal readonly record struct InboxNoteRoute(Guid Id);

[LucentRoute(typeof(LightNotesRoutes), "/archive", Id = "archive", Parent = typeof(WorkspaceRoute))]
internal readonly record struct ArchiveRoute();

[LucentRoute(typeof(LightNotesRoutes), "/archive/{id}", Id = "archive-note", Parent = typeof(ArchiveRoute))]
internal readonly record struct ArchiveNoteRoute(Guid Id);

internal static class LightNotesRouting
{
    internal static RouteTable Table { get; } = RouteTable.Create(LightNotesRoutes.Module.Patterns);

    internal static RouteDescriptorSet Descriptors { get; } =
        RouteDescriptorSet.Create(Table, [LightNotesRoutes.Module]);

    internal static ComponentRecipe Root(NoteWorkspace workspace) =>
        Context.Provide(
            workspace.Navigation,
            RouteOutlet.Create(
                Descriptors,
                static level =>
                    level.Id.Value switch
                    {
                        "workspace" => Components.RoutedWorkspaceShell(),
                        _ => throw new InvalidOperationException("Unknown root route."),
                    },
                options: new RouteOutletOptions(
                    workspace.PrepareNavigation,
                    workspace.NavigationInteraction
                )
            )
        );

    internal static ComponentRecipe Child() =>
        RouteOutlet.CreateChild(
            Descriptors,
            static level =>
                level.Id.Value switch
                {
                    "inbox" => Components.InboxRouteState(),
                    "inbox-note" => Components.InboxNoteRouteState(),
                    "archive" => Components.ArchiveRouteState(),
                    "archive-note" => Components.ArchiveNoteRouteState(),
                    _ => throw new InvalidOperationException("Unknown child route."),
                }
        );
}
