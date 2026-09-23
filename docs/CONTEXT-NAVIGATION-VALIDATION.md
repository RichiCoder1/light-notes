# Context and navigation validation

This matrix maps issue #220's lifecycle acceptance to maintained Light Notes and Lucent checks. Light Notes keeps `NoteWorkspace` as the owner of editor sessions, drafts, accepted writes, collection memory, and startup/close work. `NavigationSession` is the route authority; committed navigation applies workspace selection after publication.

## Bounded route boundary

The retained `AppView` remains mounted under the stable `/` workspace route. Collection and note suffix outlets mount collapsed route anchors beside it; those anchors instantiate and consume the exact generated `RouteContext<T>` chain and expose typed parameters in diagnostic names. They do not own or remount the visible list and editor controls. A `NavigationSession.RegisterCommitted` observer applies the published collection or note identity to the existing `NoteWorkspace` only after `Current` and the journal commit.

This boundary is deliberate for the proving migration: moving `AppView` beneath each leaf outlet would remount the current list/editor elements and weaken the app's established element, focus, and editor continuity. Removing the collapsed anchors would also remove the mounted lifetime that owns the exact typed leaf contexts, reducing the integration to generated references without proving context placement. A later product redesign can route-mount visible panes once it defines equivalent retained-element ownership; #220 does not require that rewrite.
| Acceptance | Executable evidence | What it proves |
| --- | --- | --- |
| Typed context and scoped injection | `ShellPresentationTests.RouteIntentRemainsIndependentOfWindowBreakpoint`; `ShellPresentationTests.ResponsiveShellRendersRealContentAndRetainsWorkspaceState`; package probe `tests/Probes/ContextNavigation/Hosted` | The stable workspace route and exact collection/note contexts mount generated `.lui` components. The same scoped service reaches retained and staged consumers. |
| Responsive continuity | `ShellPresentationTests.ResponsiveShellRendersRealContentAndRetainsWorkspaceState`; `WorkspaceTests.CollectionSwitchRestoresSelectionQueryAndScrollBeforeRefreshCompletes`; `FocusContinuityTests.ResponsiveCollapseRehomesFocusWithoutLosingDraftOrReplayingOnReturn` | Wide, medium, and compact changes retain draft text, undo, selection/caret, editor viewport, editor-session identity, selected row, query, list anchor, and focus without changing the committed route entry. |
| Stay, discard, and save | `WorkspaceTests.FailedRoutePreparationStaysOnTheCurrentCommittedRoute`; `WorkspaceTests.RecoveredDraftCanBeExplicitlyDiscardedToLastValidSave`; `WorkspaceTests.SwitchingRecordsSavesThePreviousDraftWithoutLosingSessionIdentity`; `PublishedDraftRecoveryTests.PublishedInvalidDraftNavigatesReopensAndDiscardsWithoutChangingValidNote` | A failed leave stays on the published route, discard restores the durable value, and switching notes saves through the existing writer while preserving editor ownership. |
| Recoverable save failure | `WorkspaceTests.CollectionNavigationRetainsPendingDraftAndReportsRecoveryWriteFailureTruthfully`; `WorkspaceTests.AutosaveValidationFailureKeepsTheDraftAndRetrySavesTheCorrection`; `NoteStoreTests.FailedRecoveryWriteDeclinesCloseUntilOriginalSnapshotRetries` | Invalid drafts remain navigable and recoverable, storage failures remain visible, and retry uses the retained snapshot. |
| Supersession and accepted writes | `WorkspaceTests.BackAndForwardApplyWorkspaceStateOnlyAfterTheSessionCommits`; `WorkspaceTests.LateAutosaveCompletionDoesNotMarkANewerDraftSavedOrResetItsEditor`; `WorkspaceTests.CancelledCloseStopsWaitingButAnAcceptedSaveStillCompletes`; `NoteStoreTests.CancellationAfterAcceptanceCannotSilentlyRemoveWrite`; hosted package probe | Only committed Back/Forward updates workspace state. Superseded navigation cannot publish stale state, and accepted writes survive route replacement, cancellation, and close negotiation. |
| Close decline and retry | `WorkspaceTests.DeclinedCloseReenablesCommandsWhenTheWindowRemainsOpen`; `NoteStoreTests.CloseWaitsForPendingAcceptedSaveAndThenRejectsAdmission`; `NoteStoreTests.FailedAcceptedWriteDeclinesCloseUntilSameIdRetrySucceeds`; `PublishedPersistenceTests.PublishedCaptureAutosaveAndReopenRetainsTheEditedNote` | A declined close leaves the workspace usable, retry drains the accepted write, and an ordinary desktop close persists before a clean reopen. |
| Provider lifetime and cleanup | Hosted package probe; `HostingTests.OfficialHostCreatesOneScopedModelAndReleasesUiBeforeServices`; `HostingTests.StartupFailureStillStopsAndDisposesTheOfficialHost`; `HostingTests.StopCompositionScopeAndHostFailuresAreAllPreserved` | Staged consumers share the scoped service, generated transient resolution occurs once per mounted suffix, UI-owned cleanup precedes provider disposal, async provider cleanup runs exactly once, and fatal cleanup preserves independent failures. |
| Stable work and retained root | Hosted package probe; `MountRequirementContracts.MountShapesReportAllocationsAndStableWorkPerformsNoFurtherLookup` | Repeated drains, context dumps, semantic snapshots, and layout perform no additional service lookup. Measured leaf remounts retain the root and create exactly one new transient per suffix. |

## Official package evidence — September 23, 2026

Validated Lucent version: `0.3.0-dev.86.1`, published from Lucent commit `6c4573b8b79550eb42d3351d4d43fe24062bdbe3` by successful workflow [35785516739](https://github.com/RichiCoder1/lucent/actions/runs/35785516739). Its managed, package-verification, and immutable-publish jobs all completed successfully.

- Light Notes Release tests: 44 passed; the separate opt-in projection characterization was skipped.
- Storage tests: 22 passed.
- Published desktop smoke: 2 passed using temporary synthetic profiles. `PublishedResponsiveCollapseRehomesFocusWithoutReplayingIt` covered routed note selection and focus continuity across responsive branch changes. `PublishedInvalidDraftNavigatesReopensAndDiscardsWithoutChangingValidNote` covered Inbox/Archive navigation, draft recovery, reopen and discard. Forward has no exposed desktop command; the managed Back/Forward contract covers both journal directions.
- Real Light Notes win-x64 NativeAOT publish: passed. `LightNotes.exe` SHA-256 is `A62AED3F74B08EA423F8EB45A82910278AB8AB075396E2789C388398219E44DF`.
- Lucent package verification includes the hosted context/navigation package proof under managed and win-x64 NativeAOT execution.

Retained evidence:

- Official desktop TRX and captures: `artifacts/desktop/dev-86.1/`
- Official NativeAOT app: `artifacts/publish/`

## Earlier package evidence — 0.3.0-dev.76.1

Validated Lucent version: `0.3.0-dev.76.1`, published from Lucent commit `b3f3d59c91352b89761b9aefde42ef1c149b6e77` by successful workflow [34822437908](https://github.com/RichiCoder1/lucent/actions/runs/34822437908). Its managed, package-verification, and immutable-publish jobs all completed successfully.

- Light Notes Release tests: 44 passed; the separate opt-in projection characterization was skipped.
- Storage tests: 22 passed.
- Published desktop smoke: 4 passed using temporary synthetic profiles. It covered responsive collection/editor activation and compact Back, Inbox/Archive draft recovery, autosave before close and reopen, and archive/restore interaction. Forward has no exposed desktop command; the managed Back/Forward contract covers both journal directions.
- Real Light Notes win-x64 NativeAOT publish: passed. `LightNotes.exe` SHA-256 is `6B5ED0B18A396A14B2006535AAB2CEA194C74466AB2BEB0B06EC69A700213EB9`.
- Lucent package verification includes the hosted context/navigation package proof under managed and win-x64 NativeAOT execution.

Retained evidence:

- Official desktop TRX and captures: `artifacts/desktop/dev-76.1/`
- Official NativeAOT app: `artifacts/publish/`
- Prepublication allocation characterization: sibling Lucent artifact `artifacts/context-navigation-hosted/a772a471e4504571a42c2ee3e1578de8/`. On the source-equivalent candidate, sixteen measured leaf remounts retained the root and created exactly sixteen new transients; observed allocation was 22,807 bytes per remount under managed execution and 22,575 under NativeAOT. These are informational measurements without timing or allocation thresholds.
