using LightNotes.Storage;

namespace LightNotes;

internal readonly record struct OwnedDraftContent(
    Guid Id,
    NoteKind Kind,
    string Title,
    string? Url,
    string Body
);

/// <summary>Owns one ordered acknowledgement stream per note draft.</summary>
internal sealed class NoteDraftWriter(
    INoteWorkspaceStorage storage,
    Func<OwnedDraftContent, string?> validate,
    Action<NoteRecord> saved,
    Func<Guid, Exception, Task> failed,
    Action changed
)
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private long _nextVersion;

    public bool IsWriting => _entries.Values.Any(entry => entry.IsWriting);

    public bool Has(Guid id) => _entries.ContainsKey(id);

    public bool IsDurable(Guid id) =>
        _entries.TryGetValue(id, out var entry) && entry.RecoveryDurableVersion == entry.Version;

    public Exception? Failure(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Failure : null;

    public long CurrentVersion(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Version : 0;

    public OwnedDraftContent? Content(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Content : null;

    public void Restore(NoteRecoveryDraft draft)
    {
        var version = checked(++_nextVersion);
        _entries[draft.Id] = new(
            new(draft.Id, draft.Kind, draft.Title, draft.Url, draft.Body),
            draft.BaseRevision,
            version
        )
        {
            HasRecoveryRecord = true,
            RecoveryDurableVersion = version,
        };
        changed();
    }

    public long Observe(OwnedDraftContent content, long baseRevision)
    {
        if (_entries.TryGetValue(content.Id, out var entry))
        {
            if (entry.Content == content)
                return entry.Version;
            entry.Content = content;
            entry.Version = checked(++_nextVersion);
            entry.Failure = null;
        }
        else
        {
            entry = new(content, baseRevision, checked(++_nextVersion));
            _entries.Add(content.Id, entry);
        }
        changed();
        return entry.Version;
    }

    public Task RequestAsync(Guid id, long version)
    {
        if (!_entries.TryGetValue(id, out var entry) || version > entry.Version)
            return Task.CompletedTask;
        entry.RequestedVersion = Math.Max(entry.RequestedVersion, version);
        if (entry.Failure is not null)
            return Task.CompletedTask;
        if (entry.Writer.IsCompleted)
        {
            entry.IsWriting = true;
            entry.Writer = WriteLoopAsync(id, entry);
            changed();
        }
        return entry.Writer;
    }

    public void AcceptConflict(Guid id, long latestRevision)
    {
        if (!_entries.TryGetValue(id, out var entry))
            return;
        entry.BaseRevision = latestRevision;
        entry.Failure = null;
        changed();
    }

    public void Remove(Guid id)
    {
        if (_entries.Remove(id))
            changed();
    }

    public bool TryRemoveUnpersisted(Guid id)
    {
        if (
            !_entries.TryGetValue(id, out var entry)
            || entry.IsWriting
            || entry.AcknowledgedVersion != 0
            || entry.HasRecoveryRecord
        )
            return false;
        _entries.Remove(id);
        changed();
        return true;
    }

    public void Reconcile(
        IReadOnlyList<NoteRecoveryDraft> recoveries,
        IReadOnlyList<NoteRecord> notes
    )
    {
        var recoveryById = recoveries.ToDictionary(draft => draft.Id);
        var noteById = notes.ToDictionary(note => note.Id);
        foreach (var pair in _entries.ToArray())
        {
            var id = pair.Key;
            var entry = pair.Value;
            if (
                recoveryById.TryGetValue(id, out var recovery)
                && recovery.BaseRevision == entry.BaseRevision
                && Same(entry.Content, recovery)
            )
            {
                entry.HasRecoveryRecord = true;
                entry.RecoveryDurableVersion = entry.Version;
                entry.AcknowledgedVersion = Math.Max(entry.AcknowledgedVersion, entry.Version);
                entry.Failure = null;
                continue;
            }
            if (noteById.TryGetValue(id, out var note) && Same(entry.Content, note))
                _entries.Remove(id);
        }
        changed();
    }

    public Task DrainAsync() =>
        Task.WhenAll(_entries.Values.Select(entry => entry.Writer).ToArray());

    public Task DrainAsync(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Writer : Task.CompletedTask;

    private async Task WriteLoopAsync(Guid id, Entry entry)
    {
        try
        {
            while (_entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
            {
                if (entry.RequestedVersion <= entry.AcknowledgedVersion)
                    return;
                if (entry.Version > entry.RequestedVersion)
                    return;

                var snapshot = new Snapshot(
                    entry.Version,
                    entry.Content,
                    entry.BaseRevision,
                    entry.HasRecoveryRecord
                );
                try
                {
                    if (validate(snapshot.Content) is not null)
                    {
                        await storage.SaveRecoveryDraftAsync(ToDraft(snapshot));
                        entry.HasRecoveryRecord = true;
                        entry.AcknowledgedVersion = Math.Max(
                            entry.AcknowledgedVersion,
                            snapshot.Version
                        );
                        if (entry.Version == snapshot.Version)
                            entry.RecoveryDurableVersion = snapshot.Version;
                    }
                    else
                    {
                        var draft = ToDraft(snapshot);
                        var record = snapshot.HadRecoveryRecord
                            ? await storage.SaveAndClearRecoveryAsync(draft)
                            : await storage.SaveAsync(draft);
                        saved(record);
                        entry.AcknowledgedVersion = Math.Max(
                            entry.AcknowledgedVersion,
                            snapshot.Version
                        );
                        entry.BaseRevision = record.Revision;
                        entry.HasRecoveryRecord = false;
                        entry.RecoveryDurableVersion = 0;
                        if (entry.Version == snapshot.Version)
                        {
                            _entries.Remove(id);
                            changed();
                            return;
                        }
                    }
                    entry.Failure = null;
                    changed();
                }
                catch (Exception error)
                {
                    entry.Failure = error;
                    changed();
                    await failed(id, error);
                    return;
                }
            }
        }
        finally
        {
            entry.IsWriting = false;
            changed();
        }
    }

    private static NoteDraft ToDraft(Snapshot snapshot) =>
        new(
            snapshot.Content.Id,
            snapshot.Content.Kind,
            snapshot.Content.Title,
            snapshot.Content.Url,
            snapshot.Content.Body,
            snapshot.BaseRevision
        );

    private static bool Same(OwnedDraftContent content, NoteRecoveryDraft draft) =>
        content.Id == draft.Id
        && content.Kind == draft.Kind
        && content.Title == draft.Title
        && content.Url == draft.Url
        && content.Body == draft.Body;

    private static bool Same(OwnedDraftContent content, NoteRecord note) =>
        content.Id == note.Id
        && content.Kind == note.Kind
        && content.Title == note.Title
        && content.Url == note.Url
        && content.Body == note.Body;

    private sealed class Entry(OwnedDraftContent content, long baseRevision, long version)
    {
        public OwnedDraftContent Content { get; set; } = content;
        public long BaseRevision { get; set; } = baseRevision;
        public long Version { get; set; } = version;
        public long RequestedVersion { get; set; }
        public long AcknowledgedVersion { get; set; }
        public long RecoveryDurableVersion { get; set; }
        public bool HasRecoveryRecord { get; set; }
        public Exception? Failure { get; set; }
        public bool IsWriting { get; set; }
        public Task Writer { get; set; } = Task.CompletedTask;
    }

    private readonly record struct Snapshot(
        long Version,
        OwnedDraftContent Content,
        long BaseRevision,
        bool HadRecoveryRecord
    );
}
