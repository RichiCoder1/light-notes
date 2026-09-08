# Responsive layout

Light Notes uses Lucent window breakpoints and parameterized `.lui` styles. The design is tracked in [Light Notes #3](https://github.com/RichiCoder1/light-notes/issues/3) and the [Lucent style-driven layout plan](https://github.com/RichiCoder1/lucent/blob/main/docs/plans/style-driven-layout.md).

| Logical window width | Presentation |
| --- | --- |
| Below 840 | Compact: navigation is collapsed; collection or editor fills the workspace according to route intent. |
| 840 through less than 1060 | Medium: navigation rail, collection and editor share the workspace. |
| 1060 and above | Wide: full navigation, collection and editor. |

`LightNotesBreakpoints` declares Medium and Wide once. The workspace owns a `WindowBreakpoints` reader and registers it on the root Layout. Named parameterized styles read `IsActive` to choose pane widths, spacing, visibility and arrangement. Thresholds are inclusive and use logical window pixels: DPI, padding and the size of a nested pane do not change their meaning. Container queries are deferred.

The pane and editor tree stays mounted across width changes. Responsive hiding uses `Participation.Collapsed`, preserving editor sessions, selection and viewport state while removing the hidden subtree from layout and accessibility. Virtualized rows still follow the framework's realization lifetime; offscreen rows may unmount. Actual data changes may also add or remove content. Numeric layout rules and breakpoint conditions that control geometry or participation belong in styles; C# owns route intent, selection, commands, focus requests, draft recovery, autosave and services.

Selecting a note records editor-route intent even when both panes are visible. Returning to the collection records collection-route intent. A subsequent compact resize presents that route without reconstructing the editor or losing a draft. Focus actions remain subject to the target's current availability; hidden controls must not steal focus.

The existing Light Notes palette and control presentation remain the design baseline. This migration formalizes responsive behavior rather than redesigning the application or changing data formats. Tests use temporary storage and verify geometry, editor continuity, route intent, collection scroll/query/selection and invalid-address recovery through width and DPI transitions.
