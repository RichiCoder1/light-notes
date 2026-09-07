# Light Notes review

Review the daily-use workflow and presentation refinement tracked in [Lucent #81](https://github.com/RichiCoder1/lucent/issues/81) and [#82](https://github.com/RichiCoder1/lucent/issues/82). This checkpoint covers the app experience and the framework/`.lui` authoring experience. The linked issues record exact source, package and verification results; a review checklist is not a claim that those checks have run.

## Open an isolated review workspace

From the repository root:

```powershell
./tools/Build.ps1 -Publish
./tools/Review.ps1
```

The launcher uses `artifacts/review/data`, separate from your normal notes. Its synthetic SQLite dataset includes similar titles, a long title and URL, an archived note, and a long multiline note with Unicode. It refuses to seed over an existing database. Rerunning keeps your review edits; `-Fresh` creates a new profile. `-PrepareOnly` prepares the profile without opening a window. Keep the executable together with its published native libraries and notices.

## App pass

Start wide, then narrow through the medium rail into a single-pane window. The intended client widths are wide at 1060+, medium at 840–1059, and compact at 480–839 logical pixels. The minimum client size is 480 × 520. Resize in both directions while editing. Use compact Back, then reopen the same item: the draft, selection and undo history should remain useful.

Try these together rather than treating them as release gates:

- Capture a thought and a URL. Edit the title, optional address and multiline note; pause for autosave, keep typing through a save, use Save now, archive, restore, close and reopen. Open the current address in your browser.
- Compare the selected row with keyboard focus. Tab through the shell, use Ctrl+N for capture, Ctrl+F for search and Ctrl+S to save. Check whether the focus destination after Back feels right.
- Search similar titles, clear the query, and try a phrase with no results. Switch between Inbox and Archive at compact width too.
- Give a saved note an empty title or an incomplete address such as `https://`. Pause for autosave, switch to another note and collection, then return. The draft should reappear without a storage-error banner; Open alone should be disabled for an invalid address. Close and reopen to confirm recovery, then use Discard draft and confirm that the last successfully saved title, address and body return.
- Give Inbox and Archive different searches, selected notes and scroll positions. Switch between them while a delayed refresh is plausible: cached rows should appear immediately, each collection should return to its own state, and filtering out the active note should leave its editor and draft open.
- Open the longest title, address and note. Read and edit near the end, then narrow to the minimum size. Look for clipped actions, cramped text and confusing scroll ownership.
- Judge spacing, contrast, reading comfort, navigation weight, row density, empty states and save feedback. Note the width and selected item when reporting a layout problem.
- Compare pointer hover, held press, selected press and disabled states. Move between editor text and buttons, watch the idle caret, and try wheel scrolling, dragging a thumb and clicking its track. Switch Inbox/Archive while a search is present and repeat the long-note archive/restore round trip.

A helpful feedback note contains: what you were trying to do, what felt wrong, what you expected, and whether it concerns the app or a reusable framework capability. Screenshots are useful for visual feedback but optional.

## Framework and `.lui` pass

Read the app composition in this order: [AppView](../src/LightNotes/AppView.lui), [CollectionPane](../src/LightNotes/CollectionPane.lui), [EditorPane](../src/LightNotes/EditorPane.lui), and [NoteRow](../src/LightNotes/NoteRow.lui). [NoteWorkspace](../src/LightNotes/NoteWorkspace.cs) owns durable application state, per-collection browsing continuity, the shared list viewport and editor sessions; CollectionPane authors their presentation. [LightNotesTheme](../src/LightNotes/LightNotesTheme.cs) owns application tokens. Program configures hosting and the native window rather than assembling a UI tree.

The shell uses one retained row with responsive widths and participation. Collapsing a pane removes it from layout/input/semantics while retaining its mounted state. `VirtualizedList` receives the workspace-owned viewport so Inbox and Archive can exchange remembered offsets through one established mount, and the text fields share owned editor sessions. These are separate contracts: retaining text alone does not establish focus, caret or scroll continuity.

New reusable presentation values provide inset borders, device-pixel hairlines and keyboard focus rings independently of selection. Windows initial/minimum dimensions belong to the Windows host options; portable Core does not depend on SDL or window management.

Authoring questions to evaluate:

- Conditional regions introduce a layout boundary. The growing list and editor use retained participation so their flex sizing reaches the pane; an outer `if` around those sections initially left unused space. Review whether that behavior is discoverable or whether layout-transparent structural regions deserve a framework follow-up.
- Does one component per `.lui` file help navigation, or does a small composed surface create too many files?
- Text controls need deliberate cross-axis stretching and shrink bounds when content can exceed the viewport. Review whether the defaults and diagnostics make this clear, especially for long single-line values inside a column.
- Are typed tokens and `style with { ... }` readable for reactive widths, participation and enabled state? Would named responsive variants improve the common case?
- Does owning `EditorSession`, `ViewportState`, responsive constraints and focus targets in the model make lifetime clear without excessive plumbing?
- Selectable now separates its live accessible label from composed visual content. NoteRow gives the title and metadata independent styles. Review whether that distinction stays clear while authoring richer rows.
- How much can you understand and change using `.lui` alone? Record places requiring unnecessary knowledge of generated C#, the public component catalog or diagnostics.

## Known scope and follow-up notes

- Autosave waits for a 750 ms quiet period. Valid edits update the note; invalid or incomplete edits use a separate recovery record and remain available across navigation and normal close/reopen. Discard removes only that pending/recovery draft and restores the last valid save. Edits during an accepted write retain their own version and editor continuity.
- Search filters the currently loaded collection in memory. It is not a paged or indexed large-library search implementation.
- Open accepts only absolute HTTP/HTTPS links and reports failures separately from save failures. It uses the current address draft.
- This slice defines a light application palette. A complete app dark/high-contrast palette and a broad appearance walkthrough remain follow-up work; the underlying framework settings do not automatically supply application token variants.
- Rounded fields and stronger typography now use reusable presentation properties. A reusable icon/vector seam remains future work. The medium rail keeps full destination names. Review whether icons would materially help before adding a framework dependency or asset pipeline.
- Storage failures keep the draft and retry path, but error presentation still exposes some store-level detail. Validated backup restoration is available through the maintenance command into a new workspace. There is no full recovery/settings UI, import merge, sync or encryption feature.
- Alt+Left is not bound globally: the current command scope consumes disabled declared shortcuts too. Compact Back and Ctrl+F provide collection navigation; a compact-only Back chord can be considered with the remaining keyboard workflow.
- UI authoring still combines Lucent style declarations and C# expressions. In particular, types and statically imported property names can collide; explicit type qualification may be needed. This is an authoring ergonomics note, not a claim that `.lui` is a CSS implementation.
- Automated accessibility rule scans complement keyboard and screen-reader review; they do not perform the manual Accessibility Insights walkthrough. Offline renderer captures likewise do not prove native focus or an unoccluded desktop screenshot.

Use the review to prioritize the next chunk. Do not grow #80 into a component registry, rich-text editor, full application catalog or release certification exercise.

## Automated evidence

The maintained desktop suite covers capture/autosave/reopen, invalid-draft recovery and discard, responsive focus, targeted Axe.Windows scans, native cursor/caret behavior, and long-note scrolling with repeated Archive/Restore. Current source/package identities and results live in the linked GitHub issues. Renderer captures, targeted automated scans, and native input tests each establish different behavior; none claims a broad manual appearance, screen-reader, DPI or Accessibility Insights walkthrough.

## Interaction feedback follow-up

The owner review opened [Lucent #91–#94](https://github.com/RichiCoder1/lucent/issues/91): long-note responsiveness and the reported Archive exit, readable hover/press states and alignment, native caret/cursor behavior, and default themeable scrollbars. Lucent owns routing, layout, shaping, scrollbar geometry and Windows caret/cursor transport; Light Notes supplies warm application styles and collection/search policy.

The scrollbar is initially vertical, with a stable gutter and app-provided default/hover/pressed brushes. The same viewport drives wheel, track, thumb and accessibility scrolling. Its `.lui` style keys are in `CollectionPane` and `EditorPane`; platform-specific appearances can replace these values without replacing scroll behavior. Horizontal visual scrollbars and full app dark/high-contrast themes remain future work.

The managed app suite covers draft write ordering, failure/retry, collection continuity, archive/restore and rendered interaction states. The previously reported Archive exit has no confirmed root cause. If it recurs, preserve `last-crash.txt` from the active data directory, when present, and report the preceding action; a successful automated round trip alone does not establish the cause of an intermittent exit.

A component's combined state rule can outrank an authored single-state rule. NoteRow and Navigation explicitly style Selected | FocusVisible to avoid inheriting the framework focus palette. The framework priority contract is unchanged; see the framework language guide for the winner rules and matching compound-state examples ([Lucent #95](https://github.com/RichiCoder1/lucent/issues/95)). The bounded Skia text probe found no RGB/BGR-specific coverage in the tested CPU path, so grayscale antialiasing remains the default; physical-panel/DPI text tuning remains a separate review.

## Desktop recovery and menu review

Try an unfinished address or empty title, switch collections, and return. The draft should remain editable and report when its recovery record is saved. Close and reopen to check recovery, then use Discard draft to return to the last valid autosave. Each collection should retain its own selection, search text and scroll position while switching immediately through cached records.

Right-click a different note: its outline marks the menu target while the existing note remains selected and open. Open note changes that selection explicitly; Archive/Restore acts on the target. Text fields offer the standard editing menu. Check keyboard invocation, Escape, outside-click dismissal and menus near window/screen edges. The first menu presenter uses Lucent-rendered popup windows; native Windows presentation remains an opt-in future slice.

For the framework and `.lui` pass, review `NoteRow`'s local `menuOpen` state and `ContextMenu.onOpenChanged`, `NoteMenu`'s command content, and `NoteDraftWriter`'s separation from mounted components. Its menu outline uses a live concrete focus-ring value from the current light palette; changing token identity dynamically remains a [framework authoring follow-up](https://github.com/RichiCoder1/lucent/issues/102), and app theme expansion should revisit that value. Report any state that is unclear or unexpectedly lost, along with the preceding action and the active data directory's crash report when one exists.
