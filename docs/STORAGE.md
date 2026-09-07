# Local storage contract

Light Notes stores its records in an app-owned SQLite schema. `Microsoft.Data.Sqlite` is the low-level provider; the app owns all schema, migration, SQL, ordering, failure, backup, and recovery behavior. Version 1 contains link and standalone-note records. Sync, rich text, page extraction, and multi-device conflict resolution are outside this contract.

## Threading and accepted work

`NoteStore` owns one background thread and a FIFO queue. Every database operation, including open, reads, writes, backup, export, and close preparation, runs on that thread. Calling a method only validates and snapshots its arguments before placing work in the queue, so SQLite never runs synchronously on the caller or UI thread.

A cancellation token can cancel an operation before the store accepts it. Once the call returns a task for accepted work, cancellation does not silently remove that work; the task completes with its durable result or an explicit exception. Saves accepted in order are executed in that order. An unconditional save for the same ID increments the stored revision, so the latest accepted snapshot wins. Callers can use `ExpectedRevision = 0` for insert-only behavior or a positive expected revision for conditional updates.

## Failures and close

Failed accepted note writes remain unresolved inside the store and surface on their returned tasks. A later successful operation of the same kind for the same note resolves that earlier failure. Save and archive intents are tracked separately: saving text does not discard an unresolved archive request. `RetryFailedWritesAsync` replays unresolved operations with their original immutable snapshots and IDs, so retry cannot create duplicate notes.

`PrepareCloseAsync` closes admission, queues a barrier behind all accepted work, and waits for that barrier. It returns `true` only when all accepted work has drained and no note-write failure remains. A successful barrier closes the store permanently and later operations are rejected. A declined barrier automatically reopens admission so the UI can retry or replace a failed save before negotiating close again. `DisposeAsync` drains through the same barrier and throws if unresolved writes would make closing unsafe.

## Workspace drafts

The workspace debounces draft edits for 750 ms and serializes accepted saves. Background autosave keeps editing enabled and preserves editor documents, selection, caret and undo history. Save now/Ctrl+S, switching records, archive, backup and ordinary close flush the current draft first. Other coordinated workspace operations may briefly disable editing. Save feedback tracks the current draft version; an older completion cannot mark a newer edit saved. Validation and write failures preserve the draft and expose Retry. If another instance has updated the selected record, the app keeps the local text and refreshes its expected revision; Retry explicitly saves that draft over the newer record. This is a local conflict escape hatch, not collaborative editing.

## Schema and recovery

Schema version 1 is recorded in SQLite `user_version`. A new empty database is created transactionally. Any nonzero version other than 1 fails open with `UnsupportedSchemaVersionException`; the app does not guess at downgrade or future-schema compatibility.

`BackupAsync` uses SQLite's online backup API on the serialized worker. `ExportJsonAsync` writes a source-generated JSON document containing the schema version, export time, and all records, including archived records. Both operations require a destination that does not already exist, so they cannot overwrite a known-good backup or export. Each writes an owned temporary sibling file and renames it without overwrite only after the backup or serialization succeeds.

`NoteStore.RestoreAsync` validates the backup's integrity, exact v1 schema and records, copies through SQLite into an owned temporary sibling database, validates the copy, and publishes it without overwrite. It rejects an existing destination database or SQLite sidecars and preserves the recovery source. Cleanup is limited to its owned temporary files.

Run `LightNotes.exe --restore <backup.db> <new-directory>` to restore into a new workspace. The maintenance command branches before opening the default live database and refuses an existing destination directory. Set `LIGHT_NOTES_DATA_DIRECTORY` to the restored directory when you are ready to open it. Keep the original live database and associated files intact. JSON import/merge, automatic repair and future-schema migration remain deferred; recovery does not guess how to repair corrupt or unsupported input.
