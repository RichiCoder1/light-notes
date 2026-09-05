# Local storage contract

Light Notes stores its records in an app-owned SQLite schema. `Microsoft.Data.Sqlite` is the low-level provider; the app owns all schema, migration, SQL, ordering, failure, backup, and recovery behavior. Version 1 contains link and standalone-note records. Sync, rich text, page extraction, and multi-device conflict resolution are outside this contract.

## Threading and accepted work

`NoteStore` owns one background thread and a FIFO queue. Every database operation, including open, reads, writes, backup, export, and close preparation, runs on that thread. Calling a method only validates and snapshots its arguments before placing work in the queue, so SQLite never runs synchronously on the caller or UI thread.

A cancellation token can cancel an operation before the store accepts it. Once the call returns a task for accepted work, cancellation does not silently remove that work; the task completes with its durable result or an explicit exception. Saves accepted in order are executed in that order. An unconditional save for the same ID increments the stored revision, so the latest accepted snapshot wins. Callers can use `ExpectedRevision = 0` for insert-only behavior or a positive expected revision for conditional updates.

## Failures and close

Failed accepted note writes remain unresolved inside the store and surface on their returned tasks. A later successful operation of the same kind for the same note resolves that earlier failure. Save and archive intents are tracked separately: saving text does not discard an unresolved archive request. `RetryFailedWritesAsync` replays unresolved operations with their original immutable snapshots and IDs, so retry cannot create duplicate notes.

`PrepareCloseAsync` closes admission, queues a barrier behind all accepted work, and waits for that barrier. It returns `true` only when all accepted work has drained and no note-write failure remains. A successful barrier closes the store permanently and later operations are rejected. A declined barrier automatically reopens admission so the UI can retry or replace a failed save before negotiating close again. `DisposeAsync` drains through the same barrier and throws if unresolved writes would make closing unsafe.

## Workspace drafts

The app serializes capture, save, archive, selection changes and backup through its workspace. Editing is disabled while one operation is pending. Switching records and ordinary close save the current draft first. Validation and write failures preserve the draft and expose Retry. If another instance has updated the selected record, the app keeps the local text and refreshes its expected revision; Retry explicitly saves that draft over the newer record. This is a local conflict escape hatch, not collaborative editing.

## Schema and recovery

Schema version 1 is recorded in SQLite `user_version`. A new empty database is created transactionally. Any nonzero version other than 1 fails open with `UnsupportedSchemaVersionException`; the app does not guess at downgrade or future-schema compatibility.

`BackupAsync` uses SQLite's online backup API on the serialized worker. `ExportJsonAsync` writes a source-generated JSON document containing the schema version, export time, and all records, including archived records. Both operations require a destination that does not already exist, so they cannot overwrite a known-good backup or export. Each writes an owned temporary sibling file and renames it without overwrite only after the backup or serialization succeeds.

Recovery for version 1 is intentionally modest: preserve the live database, open a copied backup as a database after closing the app, or use JSON export as a human-readable interchange record. Import and automated repair are deferred until a later schema requires a defined migration path.
