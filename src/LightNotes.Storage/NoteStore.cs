using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LightNotes.Storage;

public sealed class NoteStore : IAsyncDisposable
{
    public const int SchemaVersion = 2;

    private readonly object _gate = new();
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly TaskCompletionSource<NoteStore> _initialized = NewCompletion<NoteStore>();
    private readonly TaskCompletionSource _workerStopped = NewCompletion();
    private readonly List<FailedWrite> _failedWrites = [];
    private readonly string _databasePath;
    private readonly IStorageFailureInjector? _failureInjector;
    private StoreState _state = StoreState.Opening;
    private Task<bool>? _closeTask;
    private int _unresolvedWriteFailures;

    private NoteStore(string databasePath, IStorageFailureInjector? failureInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _failureInjector = failureInjector;

        var worker = new Thread(RunWorker) { IsBackground = true, Name = "LightNotes.Storage" };
        worker.Start();
    }

    public bool HasUnresolvedWriteFailures => Volatile.Read(ref _unresolvedWriteFailures) != 0;

    public static Task<NoteStore> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default
    ) => OpenCoreAsync(databasePath, failureInjector: null, cancellationToken);

    internal static Task<NoteStore> OpenAsync(
        string databasePath,
        IStorageFailureInjector failureInjector,
        CancellationToken cancellationToken = default
    ) => OpenCoreAsync(databasePath, failureInjector, cancellationToken);

    public static Task RestoreAsync(
        string sourceBackupPath,
        string destinationDatabasePath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBackupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDatabasePath);
        return RestoreCoreAsync(
            sourceBackupPath,
            destinationDatabasePath,
            recoveryFailureInjector: null,
            cancellationToken
        );
    }

    internal static Task RestoreAsync(
        string sourceBackupPath,
        string destinationDatabasePath,
        IStorageRecoveryFailureInjector recoveryFailureInjector,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBackupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDatabasePath);
        ArgumentNullException.ThrowIfNull(recoveryFailureInjector);
        return RestoreCoreAsync(
            sourceBackupPath,
            destinationDatabasePath,
            recoveryFailureInjector,
            cancellationToken
        );
    }

    public Task<NoteRecord> SaveAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        var snapshot = draft with { };
        return EnqueueWrite(
            $"draft:{snapshot.Id:D}",
            snapshot.Id,
            connection => Save(connection, snapshot, clearRecovery: false),
            cancellationToken
        );
    }

    public Task<NoteRecord> SaveAndClearRecoveryAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        var snapshot = draft with { };
        return EnqueueWrite(
            $"draft:{snapshot.Id:D}",
            snapshot.Id,
            connection => Save(connection, snapshot, clearRecovery: true),
            cancellationToken
        );
    }

    public Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(
        NoteDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        if (draft.ExpectedRevision is null or < 1)
            throw new ArgumentOutOfRangeException(
                nameof(draft),
                "A recovery draft requires the positive revision of its last valid note."
            );
        var snapshot = draft with { };
        return EnqueueWrite(
            $"draft:{snapshot.Id:D}",
            snapshot.Id,
            connection => SaveRecoveryDraft(connection, snapshot),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<NoteRecoveryDraft>> ListRecoveryDraftsAsync(
        CancellationToken cancellationToken = default
    ) =>
        Enqueue(
            connection => (IReadOnlyList<NoteRecoveryDraft>)ReadRecoveryDrafts(connection),
            cancellationToken
        );

    public Task DiscardRecoveryDraftAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A note ID cannot be empty.", nameof(id));
        return EnqueueWrite<object?>(
            $"draft:{id:D}",
            id,
            connection =>
            {
                DeleteRecoveryDraft(connection, id);
                return null;
            },
            cancellationToken
        );
    }

    public Task<NoteRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Enqueue(connection => ReadById(connection, id), cancellationToken);

    public async Task<IReadOnlyList<NoteRecord>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default
    ) =>
        await Enqueue(
                connection => (IReadOnlyList<NoteRecord>)ReadAll(connection, includeArchived),
                cancellationToken
            )
            .ConfigureAwait(false);

    public Task<NoteRecord> ArchiveAsync(
        Guid id,
        bool archived,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default
    )
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A note ID cannot be empty.", nameof(id));
        }

        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "An expected revision must be positive."
            );
        }

        return EnqueueWrite(
            $"archive:{id:D}",
            id,
            connection => Archive(connection, id, archived, expectedRevision),
            cancellationToken
        );
    }

    public Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var destination = ValidateNewDestination(destinationPath);
        return Enqueue<object?>(
            connection =>
            {
                CreateDestinationDirectory(destination);
                if (File.Exists(destination))
                {
                    throw new IOException($"Backup destination already exists: '{destination}'.");
                }

                var temporaryPath =
                    destination
                    + "."
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                    + ".tmp";
                try
                {
                    using (var backup = new SqliteConnection(CreateConnectionString(temporaryPath)))
                    {
                        backup.Open();
                        connection.BackupDatabase(backup);
                    }

                    File.Move(temporaryPath, destination);
                    return null;
                }
                finally
                {
                    DeleteOwnedSqliteFiles(temporaryPath);
                }
            },
            cancellationToken
        );
    }

    public Task ExportJsonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        var destination = ValidateNewDestination(destinationPath);
        return Enqueue<object?>(
            connection =>
            {
                CreateDestinationDirectory(destination);
                if (File.Exists(destination))
                {
                    throw new IOException($"Export destination already exists: '{destination}'.");
                }

                var temporaryPath =
                    destination
                    + "."
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                    + ".tmp";
                try
                {
                    var document = new NoteExportDocument(
                        SchemaVersion,
                        DateTimeOffset.UtcNow,
                        ReadAll(connection, includeArchived: true)
                    );
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        document,
                        NoteExportJsonContext.Default.NoteExportDocument
                    );
                    File.WriteAllBytes(temporaryPath, bytes);
                    File.Move(temporaryPath, destination);
                    return null;
                }
                finally
                {
                    File.Delete(temporaryPath);
                }
            },
            cancellationToken
        );
    }

    public Task<WriteRetryResult> RetryFailedWritesAsync(
        CancellationToken cancellationToken = default
    ) =>
        Enqueue(
            connection =>
            {
                var failures = _failedWrites.ToArray();
                var succeeded = 0;
                foreach (var failure in failures)
                {
                    try
                    {
                        _failureInjector?.BeforeWrite(failure.NoteId);
                        failure.Retry(connection);
                        RemoveFailures(failure.Key);
                        succeeded++;
                    }
                    catch (Exception exception)
                    {
                        failure.LastException = exception;
                    }
                }

                return new WriteRetryResult(failures.Length, succeeded, _failedWrites.Count);
            },
            cancellationToken
        );

    public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_state is StoreState.Closed or StoreState.Disposed)
            {
                return Task.FromResult(true);
            }

            if (_state == StoreState.Closing)
            {
                return _closeTask!;
            }

            if (_state != StoreState.Open)
            {
                throw new NoteStoreClosedException();
            }

            _state = StoreState.Closing;
            var close = new WorkItem<bool>(connection =>
            {
                var canClose = _failedWrites.Count == 0;
                lock (_gate)
                {
                    _state = canClose ? StoreState.Closed : StoreState.Open;
                    _closeTask = null;
                    if (canClose)
                    {
                        _queue.CompleteAdding();
                    }
                }

                return canClose;
            });
            _closeTask = close.Task;
            _queue.Add(close, CancellationToken.None);
            return close.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await PrepareCloseAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The note store has unresolved write failures. Retry the failed writes before disposing it."
            );
        }

        await _workerStopped.Task.ConfigureAwait(false);
        lock (_gate)
        {
            _state = StoreState.Disposed;
        }

        _queue.Dispose();
    }

    private static async Task<NoteStore> OpenCoreAsync(
        string databasePath,
        IStorageFailureInjector? failureInjector,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = new NoteStore(databasePath, failureInjector);
        try
        {
            return await store._initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.StopAfterFailedOpenAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Task RestoreCoreAsync(
        string sourceBackupPath,
        string destinationDatabasePath,
        IStorageRecoveryFailureInjector? recoveryFailureInjector,
        CancellationToken cancellationToken
    ) =>
        Task.Run(
            () =>
                Restore(
                    sourceBackupPath,
                    destinationDatabasePath,
                    recoveryFailureInjector,
                    cancellationToken
                ),
            cancellationToken
        );

    private static void Restore(
        string sourceBackupPath,
        string destinationDatabasePath,
        IStorageRecoveryFailureInjector? recoveryFailureInjector,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ValidateExistingSourcePath(sourceBackupPath);
        var destination = ValidateNewRestoreDestinationPath(source, destinationDatabasePath);

        using var sourceConnection = new SqliteConnection(CreateReadOnlyConnectionString(source));
        try
        {
            sourceConnection.Open();
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                $"The recovery source is not a readable SQLite database: {source}.",
                exception
            );
        }

        ValidateSupportedRestoreSource(sourceConnection, source);
        cancellationToken.ThrowIfCancellationRequested();
        CreateDestinationDirectory(destination);
        EnsureNewRestoreDestination(destination);
        var temporaryPath = CreateRestoreTemporaryPath(destination);
        try
        {
            using (var restored = new SqliteConnection(CreateConnectionString(temporaryPath)))
            {
                restored.Open();
                sourceConnection.BackupDatabase(restored);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var migration = new SqliteConnection(CreateConnectionString(temporaryPath)))
            {
                migration.Open();
                InitializeSchema(migration);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (
                var validation = new SqliteConnection(CreateReadOnlyConnectionString(temporaryPath))
            )
            {
                validation.Open();
                ValidateSupportedDatabase(validation, temporaryPath);
            }

            EnsureNewRestoreDestination(destination);
            recoveryFailureInjector?.BeforePublish(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: false);
        }
        finally
        {
            CleanupRestoreTemporaryDatabase(temporaryPath, destination);
        }
    }

    private static string ValidateExistingSourcePath(string sourceBackupPath)
    {
        var source = Path.GetFullPath(sourceBackupPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"The recovery source database was not found: {source}.",
                source
            );
        }

        return source;
    }

    private static string ValidateNewRestoreDestinationPath(
        string source,
        string destinationDatabasePath
    )
    {
        var destination = Path.GetFullPath(destinationDatabasePath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The restore destination must differ from the recovery source.",
                nameof(destinationDatabasePath)
            );
        }

        EnsureNewRestoreDestination(destination);
        return destination;
    }

    private static void EnsureNewRestoreDestination(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Restore destination already exists: {destination}.");
        }

        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var sidecar = destination + suffix;
            if (File.Exists(sidecar) || Directory.Exists(sidecar))
            {
                throw new IOException(
                    $"Restore destination has an existing SQLite sidecar: {sidecar}."
                );
            }
        }
    }

    private static string CreateRestoreTemporaryPath(string destination)
    {
        var temporaryPath =
            destination
            + "."
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            + ".restore.tmp";
        if (!IsOwnedRestoreTemporaryPath(temporaryPath, destination))
        {
            throw new IOException("The generated restore temporary path was invalid.");
        }

        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new IOException($"Restore temporary path already exists: {temporaryPath}.");
        }

        return temporaryPath;
    }

    private static void CleanupRestoreTemporaryDatabase(string temporaryPath, string destination)
    {
        if (IsOwnedRestoreTemporaryPath(temporaryPath, destination))
        {
            DeleteOwnedSqliteFiles(temporaryPath);
        }
    }

    private static bool IsOwnedRestoreTemporaryPath(string temporaryPath, string destination)
    {
        var temporary = Path.GetFullPath(temporaryPath);
        var target = Path.GetFullPath(destination);
        var temporaryDirectory = Path.GetDirectoryName(temporary);
        var targetDirectory = Path.GetDirectoryName(target);
        var temporaryName = Path.GetFileName(temporary);
        var targetName = Path.GetFileName(target);
        return string.Equals(
                temporaryDirectory,
                targetDirectory,
                StringComparison.OrdinalIgnoreCase
            )
            && temporaryName.StartsWith(targetName + ".", StringComparison.Ordinal)
            && temporaryName.EndsWith(".restore.tmp", StringComparison.Ordinal);
    }

    private async Task StopAfterFailedOpenAsync()
    {
        lock (_gate)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }
        }

        await _workerStopped.Task.ConfigureAwait(false);
        _queue.Dispose();
    }

    private void RunWorker()
    {
        try
        {
            CreateDestinationDirectory(_databasePath);
            using var connection = new SqliteConnection(CreateConnectionString(_databasePath));
            connection.Open();
            InitializeSchema(connection);

            lock (_gate)
            {
                _state = StoreState.Open;
            }

            _initialized.TrySetResult(this);
            foreach (var workItem in _queue.GetConsumingEnumerable())
            {
                Execute(connection, workItem);
            }
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
            while (_queue.TryTake(out var workItem))
            {
                workItem.Fail(exception);
            }
        }
        finally
        {
            _workerStopped.TrySetResult();
        }
    }

    private void Execute(SqliteConnection connection, WorkItem workItem)
    {
        try
        {
            if (workItem.Write is not null)
            {
                _failureInjector?.BeforeWrite(workItem.Write.NoteId);
            }

            workItem.Run(connection);
            if (workItem.Write is not null)
            {
                RemoveFailures(workItem.Write.Key);
            }

            workItem.Complete();
        }
        catch (Exception exception)
        {
            if (workItem.Write is not null)
            {
                RemoveFailures(workItem.Write.Key);
                _failedWrites.Add(
                    new FailedWrite(
                        workItem.Write.Key,
                        workItem.Write.NoteId,
                        workItem.Write.Retry,
                        exception
                    )
                );
                Volatile.Write(ref _unresolvedWriteFailures, _failedWrites.Count);
            }

            workItem.Fail(exception);
        }
    }

    private Task<T> Enqueue<T>(
        Func<SqliteConnection, T> action,
        CancellationToken cancellationToken
    )
    {
        var item = new WorkItem<T>(action);
        Accept(item, cancellationToken);
        return item.Task;
    }

    private Task<T> EnqueueWrite<T>(
        string key,
        Guid noteId,
        Func<SqliteConnection, T> action,
        CancellationToken cancellationToken
    )
    {
        var item = new WorkItem<T>(
            action,
            new WriteDetails(key, noteId, connection => action(connection))
        );
        Accept(item, cancellationToken);
        return item.Task;
    }

    private void Accept(WorkItem item, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != StoreState.Open)
            {
                throw new NoteStoreClosedException();
            }

            _queue.Add(item, CancellationToken.None);
        }
    }

    private void RemoveFailures(string key)
    {
        _failedWrites.RemoveAll(failure =>
            string.Equals(failure.Key, key, StringComparison.Ordinal)
        );
        Volatile.Write(ref _unresolvedWriteFailures, _failedWrites.Count);
    }

    private static NoteRecord Save(SqliteConnection connection, NoteDraft draft, bool clearRecovery)
    {
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (draft.ExpectedRevision is null)
        {
            command.CommandText = """
                INSERT INTO notes (id, kind, title, url, body, archived, revision, created_utc, updated_utc)
                VALUES ($id, $kind, $title, $url, $body, 0, 1, $now, $now)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    title = excluded.title,
                    url = excluded.url,
                    body = excluded.body,
                    revision = notes.revision + 1,
                    updated_utc = excluded.updated_utc;
                """;
        }
        else if (draft.ExpectedRevision == 0)
        {
            command.CommandText = """
                INSERT INTO notes (id, kind, title, url, body, archived, revision, created_utc, updated_utc)
                VALUES ($id, $kind, $title, $url, $body, 0, 1, $now, $now);
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE notes
                SET kind = $kind,
                    title = $title,
                    url = $url,
                    body = $body,
                    revision = revision + 1,
                    updated_utc = $now
                WHERE id = $id AND revision = $expected_revision;
                """;
            command.Parameters.AddWithValue("$expected_revision", draft.ExpectedRevision.Value);
        }

        AddDraftParameters(command, draft, now);
        try
        {
            var changed = command.ExecuteNonQuery();
            if (changed != 1)
            {
                throw new NoteConcurrencyException(draft.Id, draft.ExpectedRevision!.Value);
            }

            var record =
                ReadById(connection, draft.Id, transaction)
                ?? throw new InvalidOperationException("The saved note could not be read back.");
            if (clearRecovery)
                DeleteRecoveryDraft(connection, draft.Id, transaction);
            transaction.Commit();
            return record;
        }
        catch (SqliteException exception)
            when (draft.ExpectedRevision == 0 && exception.SqliteErrorCode == 19)
        {
            throw new NoteConcurrencyException(draft.Id, 0);
        }
    }

    private static NoteRecoveryDraft SaveRecoveryDraft(SqliteConnection connection, NoteDraft draft)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recovery_drafts (note_id, kind, title, url, body, base_revision, updated_utc)
            VALUES ($id, $kind, $title, $url, $body, $base_revision, $now)
            ON CONFLICT(note_id) DO UPDATE SET
                kind = excluded.kind,
                title = excluded.title,
                url = excluded.url,
                body = excluded.body,
                base_revision = excluded.base_revision,
                updated_utc = excluded.updated_utc;
            """;
        AddDraftParameters(command, draft, now);
        command.Parameters.AddWithValue("$base_revision", draft.ExpectedRevision!.Value);
        command.ExecuteNonQuery();
        return new(
            draft.Id,
            draft.Kind,
            draft.Title,
            draft.Url,
            draft.Body,
            draft.ExpectedRevision.Value,
            DateTimeOffset.Parse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        );
    }

    private static List<NoteRecoveryDraft> ReadRecoveryDrafts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, kind, title, url, body, base_revision, updated_utc
            FROM recovery_drafts
            ORDER BY updated_utc DESC, note_id ASC;
            """;
        using var reader = command.ExecuteReader();
        var drafts = new List<NoteRecoveryDraft>();
        while (reader.Read())
        {
            drafts.Add(
                new(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    (NoteKind)reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    DateTimeOffset.Parse(
                        reader.GetString(6),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind
                    )
                )
            );
        }
        return drafts;
    }

    private static void DeleteRecoveryDraft(
        SqliteConnection connection,
        Guid id,
        SqliteTransaction? transaction = null
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM recovery_drafts WHERE note_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static NoteRecord Archive(
        SqliteConnection connection,
        Guid id,
        bool archived,
        long? expectedRevision
    )
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedRevision is null
            ? "UPDATE notes SET archived = $archived, revision = revision + 1, updated_utc = $now WHERE id = $id;"
            : "UPDATE notes SET archived = $archived, revision = revision + 1, updated_utc = $now WHERE id = $id AND revision = $expected_revision;";
        command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        );
        if (expectedRevision is not null)
        {
            command.Parameters.AddWithValue("$expected_revision", expectedRevision.Value);
        }

        if (command.ExecuteNonQuery() != 1)
        {
            throw new NoteConcurrencyException(id, expectedRevision ?? 0);
        }

        var record =
            ReadById(connection, id, transaction)
            ?? throw new InvalidOperationException("The archived note could not be read back.");
        transaction.Commit();
        return record;
    }

    private static NoteRecord? ReadById(
        SqliteConnection connection,
        Guid id,
        SqliteTransaction? transaction = null
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, kind, title, url, body, archived, revision, created_utc, updated_utc
            FROM notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    private static List<NoteRecord> ReadAll(SqliteConnection connection, bool includeArchived)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, title, url, body, archived, revision, created_utc, updated_utc
            FROM notes
            WHERE $include_archived = 1 OR archived = 0
            ORDER BY updated_utc DESC, id ASC;
            """;
        command.Parameters.AddWithValue("$include_archived", includeArchived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var records = new List<NoteRecord>();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static NoteRecord ReadRecord(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            (NoteKind)reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) != 0,
            reader.GetInt64(6),
            DateTimeOffset.Parse(
                reader.GetString(7),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            ),
            DateTimeOffset.Parse(
                reader.GetString(8),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )
        );

    private static void AddDraftParameters(SqliteCommand command, NoteDraft draft, string now)
    {
        command.Parameters.AddWithValue(
            "$id",
            draft.Id.ToString("D", CultureInfo.InvariantCulture)
        );
        command.Parameters.AddWithValue("$kind", (int)draft.Kind);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$url", (object?)draft.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", draft.Body);
        command.Parameters.AddWithValue("$now", now);
    }

    private static void ValidateSupportedDatabase(SqliteConnection connection, string databasePath)
    {
        try
        {
            var version = ReadSchemaVersion(connection);
            if (version != SchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(version, SchemaVersion);
            }

            ValidateIntegrity(connection, databasePath);
            ValidateNotesSchema(connection, databasePath);
            ValidateRecoveryDraftsSchema(connection, databasePath);
            foreach (var record in ReadAll(connection, includeArchived: true))
            {
                ValidateRecoveredRecord(record, databasePath);
            }
        }
        catch (UnsupportedSchemaVersionException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is SqliteException
                        or FormatException
                        or OverflowException
                        or InvalidCastException
            )
        {
            throw new InvalidDataException(
                $"The recovery database failed validation: {databasePath}.",
                exception
            );
        }
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ValidateIntegrity(SqliteConnection connection, string databasePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException(
                $"The recovery database returned no integrity result: {databasePath}."
            );
        }

        do
        {
            var result = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The recovery database failed SQLite integrity_check: {result}."
                );
            }
        } while (reader.Read());
    }

    private static void ValidateNotesSchema(SqliteConnection connection, string databasePath)
    {
        using (var objectCommand = connection.CreateCommand())
        {
            objectCommand.CommandText = "SELECT type FROM sqlite_master WHERE name = $name;";
            objectCommand.Parameters.AddWithValue("$name", "notes");
            if (
                !string.Equals(
                    objectCommand.ExecuteScalar()?.ToString(),
                    "table",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    $"The recovery database does not contain the supported notes table: {databasePath}."
                );
            }
        }

        var expected = new[]
        {
            (Name: "id", Type: "TEXT", NotNull: 1, PrimaryKey: 1),
            (Name: "kind", Type: "INTEGER", NotNull: 1, PrimaryKey: 0),
            (Name: "title", Type: "TEXT", NotNull: 1, PrimaryKey: 0),
            (Name: "url", Type: "TEXT", NotNull: 0, PrimaryKey: 0),
            (Name: "body", Type: "TEXT", NotNull: 1, PrimaryKey: 0),
            (Name: "archived", Type: "INTEGER", NotNull: 1, PrimaryKey: 0),
            (Name: "revision", Type: "INTEGER", NotNull: 1, PrimaryKey: 0),
            (Name: "created_utc", Type: "TEXT", NotNull: 1, PrimaryKey: 0),
            (Name: "updated_utc", Type: "TEXT", NotNull: 1, PrimaryKey: 0),
        };

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(notes);";
        using var reader = command.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    $"The recovery database has unsupported notes columns: {databasePath}."
                );
            }

            var actual = expected[index];
            if (
                !string.Equals(reader.GetString(1), actual.Name, StringComparison.Ordinal)
                || !string.Equals(
                    reader.GetString(2),
                    actual.Type,
                    StringComparison.OrdinalIgnoreCase
                )
                || reader.GetInt32(3) != actual.NotNull
                || reader.GetInt32(5) != actual.PrimaryKey
            )
            {
                throw new InvalidDataException(
                    $"The recovery database has an unsupported notes schema: {databasePath}."
                );
            }

            index++;
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                $"The recovery database has an incomplete notes schema: {databasePath}."
            );
        }
    }

    private static void ValidateRecoveredRecord(NoteRecord record, string databasePath)
    {
        if (record.Id == Guid.Empty || !Enum.IsDefined(record.Kind) || record.Revision < 1)
        {
            throw new InvalidDataException(
                $"The recovery database contains an invalid note record: {databasePath}."
            );
        }
    }

    private static void ValidateSupportedRestoreSource(
        SqliteConnection connection,
        string databasePath
    )
    {
        try
        {
            var version = ReadSchemaVersion(connection);
            if (version is not (1 or SchemaVersion))
                throw new UnsupportedSchemaVersionException(version, SchemaVersion);

            ValidateIntegrity(connection, databasePath);
            ValidateNotesSchema(connection, databasePath);
            if (version == SchemaVersion)
                ValidateRecoveryDraftsSchema(connection, databasePath);
            foreach (var record in ReadAll(connection, includeArchived: true))
                ValidateRecoveredRecord(record, databasePath);
        }
        catch (UnsupportedSchemaVersionException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is SqliteException
                        or FormatException
                        or OverflowException
                        or InvalidCastException
            )
        {
            throw new InvalidDataException(
                $"The recovery database failed validation: {databasePath}.",
                exception
            );
        }
    }

    private static void ValidateRecoveryDraftsSchema(
        SqliteConnection connection,
        string databasePath
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('recovery_drafts');";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 7)
            throw new InvalidDataException(
                $"The recovery database has an unsupported recovery-drafts schema: {databasePath}."
            );
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version is not (0 or 1) && version != SchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(version, SchemaVersion);
            }

            if (version == SchemaVersion)
            {
                return;
            }

            if (version == 1)
            {
                using var migrationTransaction = connection.BeginTransaction();
                using var migration = connection.CreateCommand();
                migration.Transaction = migrationTransaction;
                migration.CommandText = """
                    CREATE TABLE recovery_drafts (
                        note_id TEXT NOT NULL PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
                        kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
                        title TEXT NOT NULL,
                        url TEXT NULL,
                        body TEXT NOT NULL,
                        base_revision INTEGER NOT NULL CHECK (base_revision > 0),
                        updated_utc TEXT NOT NULL
                    );
                    PRAGMA user_version = 2;
                    """;
                migration.ExecuteNonQuery();
                migrationTransaction.Commit();
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE notes (
                id TEXT NOT NULL PRIMARY KEY,
                kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
                title TEXT NOT NULL,
                url TEXT NULL,
                body TEXT NOT NULL,
                archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1)),
                revision INTEGER NOT NULL CHECK (revision > 0),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE recovery_drafts (
                note_id TEXT NOT NULL PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
                title TEXT NOT NULL,
                url TEXT NULL,
                body TEXT NOT NULL,
                base_revision INTEGER NOT NULL CHECK (base_revision > 0),
                updated_utc TEXT NOT NULL
            );
            PRAGMA user_version = 2;
            """;
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateDraft(NoteDraft draft)
    {
        if (draft.Id == Guid.Empty)
        {
            throw new ArgumentException("A note ID cannot be empty.", nameof(draft));
        }

        if (!Enum.IsDefined(draft.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "The note kind is invalid.");
        }

        ArgumentNullException.ThrowIfNull(draft.Title);
        ArgumentNullException.ThrowIfNull(draft.Body);
        if (draft.ExpectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft),
                "An expected revision cannot be negative."
            );
        }
    }

    private string ValidateNewDestination(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullPath, _databasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The destination must differ from the live database.",
                nameof(destinationPath)
            );
        }

        return fullPath;
    }

    private static void CreateDestinationDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void DeleteOwnedSqliteFiles(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-journal");
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }

    private static string CreateConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

    private static string CreateReadOnlyConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum StoreState
    {
        Opening,
        Open,
        Closing,
        Closed,
        Disposed,
    }

    private abstract class WorkItem
    {
        protected WorkItem(WriteDetails? write) => Write = write;

        public WriteDetails? Write { get; }

        public abstract void Run(SqliteConnection connection);

        public abstract void Complete();

        public abstract void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<SqliteConnection, T> _action;
        private readonly TaskCompletionSource<T> _completion = NewCompletion<T>();
        private T? _result;

        public WorkItem(Func<SqliteConnection, T> action, WriteDetails? write = null)
            : base(write) => _action = action;

        public Task<T> Task => _completion.Task;

        public override void Run(SqliteConnection connection) => _result = _action(connection);

        public override void Complete() => _completion.TrySetResult(_result!);

        public override void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed record WriteDetails(
        string Key,
        Guid NoteId,
        Func<SqliteConnection, object?> Retry
    );

    private sealed class FailedWrite(
        string key,
        Guid noteId,
        Func<SqliteConnection, object?> retry,
        Exception lastException
    )
    {
        public string Key { get; } = key;

        public Guid NoteId { get; } = noteId;

        public Func<SqliteConnection, object?> Retry { get; } = retry;

        public Exception LastException { get; set; } = lastException;
    }
}
