using LightNotes.Storage;

namespace LightNotes;

internal interface INoteWorkspaceStorage : IAsyncDisposable
{
    bool HasUnresolvedWriteFailures { get; }

    Task<NoteRecord> SaveAsync(NoteDraft draft);

    Task<NoteRecord?> GetAsync(Guid id);

    Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived);

    Task<NoteRecord> ArchiveAsync(Guid id, bool archived);

    Task<WriteRetryResult> RetryFailedWritesAsync();

    Task BackupAsync(string destinationPath);

    Task<bool> PrepareCloseAsync();
}

internal sealed class NoteWorkspaceStorage(NoteStore store) : INoteWorkspaceStorage
{
    public bool HasUnresolvedWriteFailures => store.HasUnresolvedWriteFailures;

    public Task<NoteRecord> SaveAsync(NoteDraft draft) => store.SaveAsync(draft);

    public Task<NoteRecord?> GetAsync(Guid id) => store.GetAsync(id);

    public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
        store.ListAsync(includeArchived);

    public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
        store.ArchiveAsync(id, archived);

    public Task<WriteRetryResult> RetryFailedWritesAsync() => store.RetryFailedWritesAsync();

    public Task BackupAsync(string destinationPath) => store.BackupAsync(destinationPath);

    public Task<bool> PrepareCloseAsync() => store.PrepareCloseAsync();

    public ValueTask DisposeAsync() => store.DisposeAsync();
}
