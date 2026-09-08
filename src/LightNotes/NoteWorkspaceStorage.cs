using LightNotes.Storage;

namespace LightNotes;

internal interface INoteWorkspaceStorage : IAsyncDisposable
{
    bool HasUnresolvedWriteFailures { get; }

    Task<NoteRecord> SaveAsync(NoteDraft draft);

    Task<NoteRecord> SaveAndClearRecoveryAsync(NoteDraft draft) => SaveAsync(draft);

    Task<NoteRecord?> GetAsync(Guid id);

    Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived);

    Task<IReadOnlyList<NoteRecoveryDraft>> ListRecoveryDraftsAsync() =>
        Task.FromResult<IReadOnlyList<NoteRecoveryDraft>>([]);

    Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(NoteDraft draft) =>
        Task.FromException<NoteRecoveryDraft>(new NotSupportedException());

    Task DiscardRecoveryDraftAsync(Guid id) => Task.CompletedTask;

    Task<NoteRecord> ArchiveAsync(Guid id, bool archived);

    Task<WriteRetryResult> RetryFailedWritesAsync();

    Task BackupAsync(string destinationPath);

    Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default);
}

internal sealed class NoteWorkspaceStorage(NoteStore store) : INoteWorkspaceStorage
{
    public bool HasUnresolvedWriteFailures => store.HasUnresolvedWriteFailures;

    public Task<NoteRecord> SaveAsync(NoteDraft draft) => store.SaveAsync(draft);

    public Task<NoteRecord> SaveAndClearRecoveryAsync(NoteDraft draft) =>
        store.SaveAndClearRecoveryAsync(draft);

    public Task<NoteRecord?> GetAsync(Guid id) => store.GetAsync(id);

    public Task<IReadOnlyList<NoteRecord>> ListAsync(bool includeArchived) =>
        store.ListAsync(includeArchived);

    public Task<IReadOnlyList<NoteRecoveryDraft>> ListRecoveryDraftsAsync() =>
        store.ListRecoveryDraftsAsync();

    public Task<NoteRecoveryDraft> SaveRecoveryDraftAsync(NoteDraft draft) =>
        store.SaveRecoveryDraftAsync(draft);

    public Task DiscardRecoveryDraftAsync(Guid id) => store.DiscardRecoveryDraftAsync(id);

    public Task<NoteRecord> ArchiveAsync(Guid id, bool archived) =>
        store.ArchiveAsync(id, archived);

    public Task<WriteRetryResult> RetryFailedWritesAsync() => store.RetryFailedWritesAsync();

    public Task BackupAsync(string destinationPath) => store.BackupAsync(destinationPath);

    public Task<bool> PrepareCloseAsync(CancellationToken cancellationToken = default) =>
        store.PrepareCloseAsync(cancellationToken);

    public ValueTask DisposeAsync() => store.DisposeAsync();
}
