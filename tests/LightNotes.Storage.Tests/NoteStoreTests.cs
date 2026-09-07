using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LightNotes.Storage.Tests;

[TestClass]
public sealed class NoteStoreTests
{
    [TestMethod]
    public async Task RecoveryDraftSurvivesRestartSeparatelyFromLastValidNote()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();

        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            var saved = await store.SaveAsync(
                new NoteDraft(id, NoteKind.Link, "Valid title", "https://example.com", "valid", 0)
            );
            await store.SaveRecoveryDraftAsync(
                new NoteDraft(id, NoteKind.Link, "", "https://", "recover me", saved.Revision)
            );
            Assert.IsTrue(await store.PrepareCloseAsync());
        }

        await using var reopened = await NoteStore.OpenAsync(temp.DatabasePath);
        Assert.AreEqual("Valid title", (await reopened.GetAsync(id))?.Title);
        var recovery = (await reopened.ListRecoveryDraftsAsync()).Single();
        Assert.AreEqual(id, recovery.Id);
        Assert.AreEqual("", recovery.Title);
        Assert.AreEqual("https://", recovery.Url);
        Assert.AreEqual("recover me", recovery.Body);
        Assert.AreEqual(1L, recovery.BaseRevision);
    }

    [TestMethod]
    public async Task SuccessfulValidSaveAtomicallyClearsRecoveryDraft()
    {
        using var temp = new TempDirectory();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath);
        var id = Guid.NewGuid();
        var saved = await store.SaveAsync(
            new NoteDraft(id, NoteKind.Note, "Valid", null, "one", 0)
        );
        await store.SaveRecoveryDraftAsync(
            new NoteDraft(id, NoteKind.Note, "", null, "unfinished", saved.Revision)
        );

        await store.SaveAndClearRecoveryAsync(
            new NoteDraft(id, NoteKind.Note, "Corrected", null, "finished", saved.Revision)
        );

        Assert.AreEqual(0, (await store.ListRecoveryDraftsAsync()).Count);
        Assert.AreEqual("Corrected", (await store.GetAsync(id))?.Title);
    }

    [TestMethod]
    public async Task VersionOneDatabaseMigratesWithoutLosingNotes()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();
        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            await store.SaveAsync(
                new NoteDraft(id, NoteKind.Note, "Before migration", null, "kept", 0)
            );
        }
        using (
            var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False")
        )
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE recovery_drafts; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        await using (var migrated = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            Assert.AreEqual("Before migration", (await migrated.GetAsync(id))?.Title);
            Assert.AreEqual(0, (await migrated.ListRecoveryDraftsAsync()).Count);
        }
    }

    [TestMethod]
    public async Task FailedRecoveryWriteDeclinesCloseUntilOriginalSnapshotRetries()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();
        long revision;
        await using (var seed = await NoteStore.OpenAsync(temp.DatabasePath))
            revision = (
                await seed.SaveAsync(new NoteDraft(id, NoteKind.Note, "Valid", null, "saved", 0))
            ).Revision;

        var injector = new FailFirstWrite();
        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath, injector))
        {
            await AssertThrowsAsync<InjectedStorageException>(() =>
                store.SaveRecoveryDraftAsync(
                    new NoteDraft(id, NoteKind.Note, "", null, "recover", revision)
                )
            );
            Assert.IsFalse(await store.PrepareCloseAsync());

            var retry = await store.RetryFailedWritesAsync();
            Assert.AreEqual(1, retry.Succeeded);
            Assert.IsTrue(await store.PrepareCloseAsync());
        }

        await using var reopened = await NoteStore.OpenAsync(temp.DatabasePath);
        Assert.AreEqual("recover", (await reopened.ListRecoveryDraftsAsync()).Single().Body);
        Assert.AreEqual("Valid", (await reopened.GetAsync(id))?.Title);
    }

    [TestMethod]
    public async Task CreateUpdateArchiveAndReopenPreservesRecords()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();

        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            var created = await store.SaveAsync(
                new NoteDraft(id, NoteKind.Link, "Example", "https://example.com", "", 0)
            );
            Assert.AreEqual(1L, created.Revision);

            var updated = await store.SaveAsync(
                new NoteDraft(id, NoteKind.Note, "Updated", null, "Body", created.Revision)
            );
            Assert.AreEqual(2L, updated.Revision);
            Assert.AreEqual("Body", updated.Body);

            var archived = await store.ArchiveAsync(id, archived: true, updated.Revision);
            Assert.IsTrue(archived.IsArchived);
            Assert.AreEqual(3L, archived.Revision);
            Assert.AreEqual(0, (await store.ListAsync()).Count);
            Assert.IsTrue(await store.PrepareCloseAsync());
        }

        await using var reopened = await NoteStore.OpenAsync(temp.DatabasePath);
        var record = await reopened.GetAsync(id);
        Assert.IsNotNull(record);
        Assert.AreEqual("Updated", record.Title);
        Assert.AreEqual("Body", record.Body);
        Assert.IsTrue(record.IsArchived);
        Assert.AreEqual(3L, record.Revision);
        Assert.AreEqual(1, (await reopened.ListAsync(includeArchived: true)).Count);
    }

    [TestMethod]
    public async Task LatestAcceptedSaveWinsWhileEarlierSaveIsDelayed()
    {
        using var temp = new TempDirectory();
        using var blocker = new BlockingFirstWrite();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath, blocker);
        var id = Guid.NewGuid();

        var first = store.SaveAsync(new NoteDraft(id, NoteKind.Note, "First", null, "one"));
        blocker.WaitUntilEntered();
        var second = store.SaveAsync(new NoteDraft(id, NoteKind.Note, "Second", null, "two"));

        blocker.Release();
        await Task.WhenAll(first, second);

        var record = await store.GetAsync(id);
        Assert.IsNotNull(record);
        Assert.AreEqual("Second", record.Title);
        Assert.AreEqual("two", record.Body);
        Assert.AreEqual(2L, record.Revision);
    }

    [TestMethod]
    public async Task CancellationBeforeAcceptanceDoesNotWrite()
    {
        using var temp = new TempDirectory();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var id = Guid.NewGuid();

        await AssertCanceledAsync(() =>
            store.SaveAsync(
                new NoteDraft(id, NoteKind.Note, "Canceled", null, ""),
                cancellation.Token
            )
        );

        Assert.IsNull(await store.GetAsync(id));
    }

    [TestMethod]
    public async Task CancellationAfterAcceptanceCannotSilentlyRemoveWrite()
    {
        using var temp = new TempDirectory();
        using var blocker = new BlockingFirstWrite();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath, blocker);
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();

        var save = store.SaveAsync(
            new NoteDraft(id, NoteKind.Note, "Accepted", null, "durable"),
            cancellation.Token
        );
        blocker.WaitUntilEntered();
        cancellation.Cancel();
        blocker.Release();

        var saved = await save;
        Assert.AreEqual(id, saved.Id);
        Assert.AreEqual("durable", (await store.GetAsync(id))?.Body);
    }

    [TestMethod]
    public async Task CloseWaitsForPendingAcceptedSaveAndThenRejectsAdmission()
    {
        using var temp = new TempDirectory();
        using var blocker = new BlockingFirstWrite();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath, blocker);

        var save = store.SaveAsync(
            new NoteDraft(Guid.NewGuid(), NoteKind.Note, "Pending", null, "")
        );
        blocker.WaitUntilEntered();
        var close = store.PrepareCloseAsync();
        Assert.IsFalse(close.IsCompleted);

        blocker.Release();
        await save;
        Assert.IsTrue(await close);
        await AssertThrowsAsync<NoteStoreClosedException>(() => store.ListAsync());
    }

    [TestMethod]
    public async Task FailedAcceptedWriteDeclinesCloseUntilSameIdRetrySucceeds()
    {
        using var temp = new TempDirectory();
        var injector = new FailFirstWrite();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath, injector);
        var draft = new NoteDraft(Guid.NewGuid(), NoteKind.Note, "Retry", null, "same snapshot", 0);

        await AssertThrowsAsync<InjectedStorageException>(() => store.SaveAsync(draft));
        Assert.IsTrue(store.HasUnresolvedWriteFailures);
        Assert.IsFalse(await store.PrepareCloseAsync());

        var saved = await store.SaveAsync(draft);
        Assert.AreEqual(1L, saved.Revision);
        Assert.IsFalse(store.HasUnresolvedWriteFailures);
        Assert.IsTrue(await store.PrepareCloseAsync());
    }

    [TestMethod]
    public async Task StoreLevelRetryReplaysOriginalSnapshotWithoutDuplicate()
    {
        using var temp = new TempDirectory();
        var injector = new FailFirstWrite();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath, injector);
        var id = Guid.NewGuid();

        await AssertThrowsAsync<InjectedStorageException>(() =>
            store.SaveAsync(new NoteDraft(id, NoteKind.Note, "Retry", null, "once", 0))
        );
        var retry = await store.RetryFailedWritesAsync();

        Assert.AreEqual(1, retry.Retried);
        Assert.AreEqual(1, retry.Succeeded);
        Assert.AreEqual(0, retry.Remaining);
        Assert.AreEqual(1, (await store.ListAsync(includeArchived: true)).Count);
        Assert.AreEqual(1L, (await store.GetAsync(id))?.Revision);
    }

    [TestMethod]
    public async Task ConditionalRevisionRejectsStaleUpdate()
    {
        using var temp = new TempDirectory();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath);
        var id = Guid.NewGuid();
        var created = await store.SaveAsync(
            new NoteDraft(id, NoteKind.Note, "Current", null, "", 0)
        );
        await store.SaveAsync(
            new NoteDraft(id, NoteKind.Note, "Newer", null, "", created.Revision)
        );

        var exception = await AssertThrowsAsync<NoteConcurrencyException>(() =>
            store.SaveAsync(new NoteDraft(id, NoteKind.Note, "Stale", null, "", created.Revision))
        );

        Assert.AreEqual(id, exception.NoteId);
        Assert.AreEqual("Newer", (await store.GetAsync(id))?.Title);
        Assert.IsFalse(await store.PrepareCloseAsync());
        var resolved = await store.SaveAsync(
            new NoteDraft(id, NoteKind.Note, "Resolved", null, "")
        );
        Assert.AreEqual("Resolved", resolved.Title);
    }

    [TestMethod]
    public async Task BackupAndJsonExportContainDurableRecords()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();
        var backupPath = Path.Combine(temp.Path, "backups", "notes.db");
        var exportPath = Path.Combine(temp.Path, "exports", "notes.json");

        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            await store.SaveAsync(new NoteDraft(id, NoteKind.Note, "Exported", null, "Body", 0));
            await store.BackupAsync(backupPath);
            await store.ExportJsonAsync(exportPath);
        }

        await using var backup = await NoteStore.OpenAsync(backupPath);
        Assert.AreEqual("Exported", (await backup.GetAsync(id))?.Title);

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(exportPath));
        Assert.AreEqual(
            NoteStore.SchemaVersion,
            json.RootElement.GetProperty("SchemaVersion").GetInt32()
        );
        Assert.AreEqual(
            id.ToString(),
            json.RootElement.GetProperty("Notes")[0].GetProperty("Id").GetString()
        );
    }

    [TestMethod]
    public async Task UnknownSchemaVersionFailsClosed()
    {
        using var temp = new TempDirectory();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = temp.DatabasePath,
            Pooling = false,
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }

        var exception = await AssertThrowsAsync<UnsupportedSchemaVersionException>(() =>
            NoteStore.OpenAsync(temp.DatabasePath)
        );
        Assert.AreEqual(3, exception.ActualVersion);
        Assert.AreEqual(NoteStore.SchemaVersion, exception.SupportedVersion);
    }

    [TestMethod]
    public async Task BackupNeverOverwritesExistingDestination()
    {
        using var temp = new TempDirectory();
        await using var store = await NoteStore.OpenAsync(temp.DatabasePath);
        await store.SaveAsync(new NoteDraft(Guid.NewGuid(), NoteKind.Note, "Stored", null, "", 0));
        var destination = Path.Combine(temp.Path, "existing.db");
        var original = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(destination, original);

        await AssertThrowsAsync<IOException>(() => store.BackupAsync(destination));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(destination));
    }

    [TestMethod]
    public async Task RestoreRoundTripPreservesArchivedMultilineRecordsAndSource()
    {
        using var temp = new TempDirectory();
        var activeId = Guid.NewGuid();
        var archivedId = Guid.NewGuid();
        var activeBody = "first line\r\nsecond line\nthird line";
        var archivedBody = "archived\r\nbody";
        var backupPath = Path.Combine(temp.Path, "backup.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");

        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            await store.SaveAsync(
                new NoteDraft(activeId, NoteKind.Note, "Active", null, activeBody, 0)
            );
            var archived = await store.SaveAsync(
                new NoteDraft(
                    archivedId,
                    NoteKind.Link,
                    "Archived",
                    "https://example.test",
                    archivedBody,
                    0
                )
            );
            await store.ArchiveAsync(archived.Id, archived: true, archived.Revision);
            await store.BackupAsync(backupPath);
        }

        var liveBytes = await File.ReadAllBytesAsync(temp.DatabasePath);
        var backupBytes = await File.ReadAllBytesAsync(backupPath);
        var sidecarPath = backupPath + "-shm";
        var sidecarBytes = new byte[] { 4, 8, 15, 16, 23, 42 };
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes);

        await NoteStore.RestoreAsync(backupPath, destinationPath);

        CollectionAssert.AreEqual(liveBytes, await File.ReadAllBytesAsync(temp.DatabasePath));
        CollectionAssert.AreEqual(backupBytes, await File.ReadAllBytesAsync(backupPath));
        CollectionAssert.AreEqual(sidecarBytes, await File.ReadAllBytesAsync(sidecarPath));
        await using var restored = await NoteStore.OpenAsync(destinationPath);
        var active = await restored.GetAsync(activeId);
        var archivedRecord = await restored.GetAsync(archivedId);
        Assert.IsNotNull(active);
        Assert.IsNotNull(archivedRecord);
        Assert.AreEqual(activeBody, active.Body);
        Assert.AreEqual(archivedBody, archivedRecord.Body);
        Assert.IsTrue(archivedRecord.IsArchived);
        Assert.AreEqual(2, (await restored.ListAsync(includeArchived: true)).Count);
    }

    [TestMethod]
    public async Task RestoreMigratesVersionOneBackupWithoutChangingItsSource()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();
        var sourcePath = Path.Combine(temp.Path, "version-one.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        await using (var seed = await NoteStore.OpenAsync(sourcePath))
            await seed.SaveAsync(
                new NoteDraft(id, NoteKind.Note, "Version one backup", null, "kept", 0)
            );
        using (var connection = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE recovery_drafts; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);

        await NoteStore.RestoreAsync(sourcePath, destinationPath);

        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        using (
            var source = new SqliteConnection(
                $"Data Source={sourcePath};Mode=ReadOnly;Pooling=False"
            )
        )
        {
            source.Open();
            using var version = source.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(1L, version.ExecuteScalar());
            using var table = source.CreateCommand();
            table.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recovery_drafts';";
            Assert.AreEqual(0L, table.ExecuteScalar());
        }
        await using var restored = await NoteStore.OpenAsync(destinationPath);
        Assert.AreEqual("Version one backup", (await restored.GetAsync(id))?.Title);
        Assert.AreEqual("kept", (await restored.GetAsync(id))?.Body);
        Assert.AreEqual(0, (await restored.ListRecoveryDraftsAsync()).Count);
    }

    [TestMethod]
    public async Task RestoreRejectsUnsupportedSchemaWithoutCreatingDestination()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "unsupported.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        using (
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString()
            )
        )
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }

        var exception = await AssertThrowsAsync<UnsupportedSchemaVersionException>(() =>
            NoteStore.RestoreAsync(sourcePath, destinationPath)
        );
        Assert.AreEqual(3, exception.ActualVersion);
        Assert.IsFalse(File.Exists(destinationPath));
        AssertNoRestoreTemporaryFiles(destinationPath);
    }

    [TestMethod]
    public async Task RestoreRejectsCorruptSourceWithoutCreatingDestination()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "corrupt.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        await File.WriteAllBytesAsync(sourcePath, new byte[] { 0, 1, 2, 3, 5, 8, 13 });

        await AssertThrowsAsync<InvalidDataException>(() =>
            NoteStore.RestoreAsync(sourcePath, destinationPath)
        );
        Assert.IsFalse(File.Exists(destinationPath));
        AssertNoRestoreTemporaryFiles(destinationPath);
    }

    [TestMethod]
    public async Task RestoreRejectsSchemaMissingNotesTable()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "missing-table.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        using (
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString()
            )
        )
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        await AssertThrowsAsync<InvalidDataException>(() =>
            NoteStore.RestoreAsync(sourcePath, destinationPath)
        );
        Assert.IsFalse(File.Exists(destinationPath));
        AssertNoRestoreTemporaryFiles(destinationPath);
    }

    [TestMethod]
    public async Task RestoreCleansOwnedTemporaryFilesWhenPublishFails()
    {
        using var temp = new TempDirectory();
        var backupPath = Path.Combine(temp.Path, "backup.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            await store.SaveAsync(
                new NoteDraft(Guid.NewGuid(), NoteKind.Note, "Stored", null, "", 0)
            );
            await store.BackupAsync(backupPath);
        }

        var injector = new FailBeforePublish();
        await AssertThrowsAsync<InjectedStorageException>(() =>
            NoteStore.RestoreAsync(backupPath, destinationPath, injector)
        );
        Assert.IsNotNull(injector.TemporaryPath);
        Assert.IsFalse(File.Exists(destinationPath));
        Assert.IsFalse(File.Exists(injector.TemporaryPath!));
        AssertNoRestoreTemporaryFiles(destinationPath);
    }

    [TestMethod]
    public async Task RestoreNeverOverwritesDestinationOrSidecars()
    {
        using var temp = new TempDirectory();
        var backupPath = Path.Combine(temp.Path, "backup.db");
        var destinationPath = Path.Combine(temp.Path, "restored", "notes.db");
        await using (var store = await NoteStore.OpenAsync(temp.DatabasePath))
        {
            await store.SaveAsync(
                new NoteDraft(Guid.NewGuid(), NoteKind.Note, "Stored", null, "", 0)
            );
            await store.BackupAsync(backupPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var destinationBytes = new byte[] { 9, 9, 7 };
        var sidecarBytes = new byte[] { 6, 6, 4 };
        await File.WriteAllBytesAsync(destinationPath, destinationBytes);
        await File.WriteAllBytesAsync(destinationPath + "-wal", sidecarBytes);

        await AssertThrowsAsync<IOException>(() =>
            NoteStore.RestoreAsync(backupPath, destinationPath)
        );
        CollectionAssert.AreEqual(destinationBytes, await File.ReadAllBytesAsync(destinationPath));
        CollectionAssert.AreEqual(
            sidecarBytes,
            await File.ReadAllBytesAsync(destinationPath + "-wal")
        );
        AssertNoRestoreTemporaryFiles(destinationPath);
    }

    private static void AssertNoRestoreTemporaryFiles(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        var pattern = Path.GetFileName(destinationPath) + ".*.restore.tmp*";
        Assert.AreEqual(0, Directory.GetFiles(directory, pattern).Length);
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
        throw new InvalidOperationException();
    }

    private static async Task AssertCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected cancellation.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LightNotes.Storage.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string DatabasePath => System.IO.Path.Combine(Path, "notes.db");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class BlockingFirstWrite : IStorageFailureInjector, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _calls;

        public void BeforeWrite(Guid noteId)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return;
            }

            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocked write.");
            }
        }

        public void WaitUntilEntered() =>
            Assert.IsTrue(
                _entered.Wait(TimeSpan.FromSeconds(10)),
                "The storage worker did not reach the write."
            );

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class FailFirstWrite : IStorageFailureInjector
    {
        private int _calls;

        public void BeforeWrite(Guid noteId)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InjectedStorageException();
            }
        }
    }

    private sealed class FailBeforePublish : IStorageRecoveryFailureInjector
    {
        public string? TemporaryPath { get; private set; }

        public void BeforePublish(string temporaryPath)
        {
            TemporaryPath = temporaryPath;
            throw new InjectedStorageException();
        }
    }

    private sealed class InjectedStorageException : Exception;
}
