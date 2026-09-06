# Component ownership

CollectionPane is the first stateful `.lui` component in Light Notes. Its readonly ViewportState is created once with the component owner. Derived declarations describe pane geometry, visibility, and search presentation; ClearSearch is an ordinary component method passed to CollectionEmpty as an Action.

The pane stays mounted across responsive layouts. Collapsed participation preserves its viewport; removing and remounting the pane would create a new one. The shell test sends wheel input to a visible row and reacquires the same logical row after layout transitions, since virtualization may recycle element identities.

NoteWorkspace still owns search/editor sessions, drafts, selected records, accepted saves, retries, shutdown preparation, routing, and shared focus targets. Do not move these into replaceable children without preserving their lifetime and save-ordering contracts.

Declaration rules: constant initializers create writable state; other unmarked expressions are read-only derived values. `[Once]` creates a writable initial copy, and `readonly` captures an initial snapshot. Locals within methods and Setup are ordinary C#. Async resources stay explicit; overlapping loads do not imply concurrent mounting.

Review whether these distinctions read clearly while editing CollectionPane. Future work can improve explicit async-resource authoring and separate shell routing/focus after those cross-pane transitions have a clear owner. Record declarations and expression-bodied markup remain future Lucent work.
