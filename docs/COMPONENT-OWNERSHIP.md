# Component ownership

CollectionPane owns derived pane geometry, visibility, and search presentation; ClearSearch is an ordinary component method passed to CollectionEmpty as an Action. Its viewport comes from the workspace because Inbox and Archive each retain browsing continuity beyond a single presentation. NoteRow owns its temporary menuOpen state through ContextMenu.onOpenChanged, keeping the right-click target cue separate from selected-note state.

The pane stays mounted across responsive layouts. Collapsed participation preserves its presentation; the workspace supplies viewport state across collection switches and remounts. A virtualized row's local menu state ends when that row is removed. The shell test sends wheel input to a visible row and reacquires the same logical row after layout transitions, since virtualization may recycle element identities.

NoteWorkspace owns search/editor sessions, per-collection selection/query/scroll, draft coordination, retries, shutdown preparation, routing, and shared focus targets. NoteDraftWriter owns each note's ordered write versions and recovery acknowledgements independently of mounted rows. Do not move these into replaceable children without preserving their lifetime and save-ordering contracts.

Declaration rules: constant initializers create writable state; other unmarked expressions are read-only derived values. `[Once]` creates a writable initial copy, and `readonly` captures an initial snapshot. Locals within methods and Setup are ordinary C#. Async resources stay explicit; overlapping loads do not imply concurrent mounting.

Review whether these distinctions read clearly while editing CollectionPane. Lucent's explicit source/fetcher AsyncValue path supports component-owned reads with retry and cancellation. Autosave uses an optional owned R3 adapter; accepted writes remain workspace-owned. Shell routing/focus can be separated after those cross-pane transitions have a clear owner. Record declarations and expression-bodied markup remain future Lucent work.
