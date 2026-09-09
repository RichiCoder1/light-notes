# Local storage contract

Light Notes stores its records in an app-owned SQLite schema. `Microsoft.Data.Sqlite` is the low-level provider; the app owns all schema, migration, SQL, ordering, failure, backup, and recovery behavior. Version 2 contains link and standalone-note records plus per-note recovery drafts. Sync, rich text, page extraction, and multi-device conflict resolution are outside this contract.

## Threading and accepted work

`NoteStore` owns one background thread and a FIFO queue. Every database operation, including open, reads, writes, backup, export, and close preparation, runs on that thread. Calling a method only validates and snapshots its arguments before placing work in the queue, so SQLite never runs synchronously on the caller or UI thread.

A cancellation token can cancel an operation before the store accepts it. Once the call returns a task for accepted work, cancellation does not silently remove that work; the task completes with its durable result or an explicit exception. Saves accepted in order are executed in that order. An unconditional save for the same ID increments the stored revision, so the latest accepted snapshot wins. Callers can use `ExpectedRevision = 0` for insert-only behavior or a positive expected revision for conditional updates.

## Failures and close

Failed accepted note writes remain unresolved inside the store and surface on their returned tasks. A later successful operation of the same kind for the same note resolves that earlier failure. Save and archive intents are tracked separately: saving text does not discard an unresolved archive request. `RetryFailedWritesAsync` replays unresolved operations with their original immutable snapshots and IDs, so retry cannot create duplicate notes.

`PrepareCloseAsync` closes admission, queues a barrier behind all accepted work, and waits for that barrier. It returns `true` only when all accepted work has drained and no note-write failure remains. A successful barrier closes the store permanently and later operations are rejected. A declined barrier automatically reopens admission so the UI can retry or replace a failed save before negotiating close again. `DisposeAsync` drains through the same barrier and throws if unresolved writes would make closing unsafe.

The workspace mirrors the close negotiation as reactive state. While the barrier is pending, editing and search commands are disabled; when the barrier declines or is cancelled, those controls are re-enabled immediately while the failure remains available through the retry action.

## Workspace drafts

The workspace debounces draft edits for 750 ms. Each note has one writer that owns immutable content snapshots, their base revision, and requested/acknowledged versions. Navigation and autosave use that same writer; older completions cannot replace newer editor content. All collection refreshes join an ordered task chain that close also drains. Background autosave keeps editing enabled and preserves editor documents, selection, caret and undo history. A valid workspace save updates the note and removes its own known recovery draft in one transaction. Ordinary store saves leave unrelated recovery records intact. An empty title or incomplete URL is ordinary inline validation: autosave writes the recovery draft separately, Open is disabled for an invalid URL, and navigation remains available. Actual write failures remain storage errors and participate in retry and close negotiation. Discard draft deletes the recovery snapshot and restores the last successfully saved note; it does not roll back earlier valid autosaves. A restored draft keeps its original base revision. If another instance has updated that record, the app keeps the local text and reports a conflict without overwriting the newer record; explicit Retry accepts the latest revision and saves the draft over it. Retrying failed storage work reconciles all affected drafts, including offscreen ones, against the durable note/recovery records before clearing failure feedback. This is a local conflict escape hatch, not collaborative editing.

## Schema and recovery

Schema version 2 is recorded in SQLite `user_version`. A new empty database is created transactionally. Version 1 databases migrate in place by adding the recovery-draft table without rewriting notes. Other unsupported versions fail open with `UnsupportedSchemaVersionException`; the app does not guess at downgrade or future-schema compatibility.

`BackupAsync` uses SQLite's online backup API on the serialized worker. `ExportJsonAsync` writes a source-generated JSON document containing the schema version, export time, and all records, including archived records. Both operations require a destination that does not already exist, so they cannot overwrite a known-good backup or export. Each writes an owned temporary sibling file and renames it without overwrite only after the backup or serialization succeeds.

`NoteStore.RestoreAsync` validates the backup's integrity, supported schema and records before copying. Version 1 backups remain supported: restore validates their exact notes schema read-only, copies through SQLite into an owned temporary sibling database, migrates only that temporary copy to version 2, then validates the completed v2 database before publishing it without overwrite. Version 2 backups also require the recovery-draft table. Restore rejects malformed or newer schemas, an existing destination database or SQLite sidecars, and never modifies the recovery source. Cleanup is limited to its owned temporary files.

Run `LightNotes.exe --restore <backup.db> <new-directory>` to restore into a new workspace. The maintenance command branches before opening the default live database and refuses an existing destination directory. Set `LIGHT_NOTES_DATA_DIRECTORY` to the restored directory when you are ready to open it. Keep the original live database and associated files intact. JSON import/merge, automatic repair and future-schema migration remain deferred; recovery does not guess how to repair corrupt or unsupported input.
