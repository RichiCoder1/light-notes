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
- Open the longest title, address and note. Read and edit near the end, then narrow to the minimum size. Look for clipped actions, cramped text and confusing scroll ownership.
- Judge spacing, contrast, reading comfort, navigation weight, row density, empty states and save feedback. Note the width and selected item when reporting a layout problem.

A helpful feedback note contains: what you were trying to do, what felt wrong, what you expected, and whether it concerns the app or a reusable framework capability. Screenshots are useful for visual feedback but optional.

## Framework and `.lui` pass

Read the app composition in this order: [AppView](../src/LightNotes/AppView.lui), [CollectionPane](../src/LightNotes/CollectionPane.lui), [EditorPane](../src/LightNotes/EditorPane.lui), and [NoteRow](../src/LightNotes/NoteRow.lui). [NoteWorkspace](../src/LightNotes/NoteWorkspace.cs) owns durable application state and shared editor sessions; CollectionPane owns its local viewport and presentation state; [LightNotesTheme](../src/LightNotes/LightNotesTheme.cs) owns application tokens. Program configures hosting and the native window rather than assembling a UI tree.

The shell uses one retained row with responsive widths and participation. Collapsing a pane removes it from layout/input/semantics while retaining its mounted state. `VirtualizedList` receives CollectionPane's mount-owned viewport, and the text fields share owned editor sessions. These are separate contracts: retaining text alone does not establish focus, caret or scroll continuity.

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

- Autosave waits for a 750 ms quiet period. Save now/Ctrl+S and save-before-switch/close remain explicit paths; edits during an accepted write retain their own version and editor continuity.
- Search filters the currently loaded collection in memory. It is not a paged or indexed large-library search implementation.
- Open accepts only absolute HTTP/HTTPS links and reports failures separately from save failures. It uses the current address draft.
- This slice defines a light application palette. A complete app dark/high-contrast palette and a broad appearance walkthrough remain follow-up work; the underlying framework settings do not automatically supply application token variants.
- Rounded fields and stronger typography now use reusable presentation properties. A reusable icon/vector seam remains future work. The medium rail keeps full destination names. Review whether icons would materially help before adding a framework dependency or asset pipeline.
- Storage failures keep the draft and retry path, but error presentation still exposes some store-level detail. Validated backup restoration is available through the maintenance command into a new workspace. There is no full recovery/settings UI, import merge, sync or encryption feature.
- Alt+Left is not bound globally: the current command scope consumes disabled declared shortcuts too. Compact Back and Ctrl+F provide collection navigation; a compact-only Back chord can be considered with the remaining keyboard workflow.
- UI authoring still combines Lucent style declarations and C# expressions. In particular, types and statically imported property names can collide; explicit type qualification may be needed. This is an authoring ergonomics note, not a claim that `.lui` is a CSS implementation.
- Automated accessibility rule scans complement keyboard and screen-reader review; they do not perform the manual Accessibility Insights walkthrough. Offline renderer captures likewise do not prove native focus or an unoccluded desktop screenshot.

Use the review to prioritize the next chunk. Do not grow #80 into a component registry, rich-text editor, full application catalog or release certification exercise.
