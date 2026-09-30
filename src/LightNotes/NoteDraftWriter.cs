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
    Action<Guid> acknowledged,
    Func<Guid, Exception, Task> failed,
    Action changed
)
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private long _nextVersion;
    private int _paused;

    public bool IsWriting => _entries.Values.Any(entry => entry.IsWriting);

    public bool Has(Guid id) => _entries.ContainsKey(id);

    public bool IsDurable(Guid id) =>
        _entries.TryGetValue(id, out var entry) && entry.RecoveryDurableVersion == entry.Version;

    public Exception? Failure(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Failure : null;

    public NoteRecoveryDraft? Recovery(Guid id) =>
        _entries.TryGetValue(id, out var entry) ? entry.RecoveryRecord : null;

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
            RecoveryRecord = draft,
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
        if (entry.Failure is not null || entry.DiscardPending || _paused != 0)
            return Task.CompletedTask;
        if (entry.Writer.IsCompleted)
        {
            entry.IsWriting = true;
            entry.Writer = WriteLoopAsync(id, entry);
            changed();
        }
        return entry.Writer;
    }

    public bool OwnsFailedWrite(Guid id, Guid writeId) =>
        writeId != Guid.Empty
        && _entries.TryGetValue(id, out var entry)
        && entry.FailedWrite?.Draft.WriteId == writeId;

    public void RequestObservedDrafts()
    {
        foreach (var pair in _entries.ToArray())
            _ = RequestAsync(pair.Key, pair.Value.Version);
    }

    public IDisposable PauseWrites()
    {
        _paused++;
        return new WritePause(this);
    }

    private void ResumeWrites()
    {
        if (--_paused != 0)
            return;
        foreach (var pair in _entries.ToArray())
            if (pair.Value.RequestedVersion > pair.Value.AcknowledgedVersion)
                _ = RequestAsync(pair.Key, pair.Value.RequestedVersion);
    }

    public long BeginDiscard(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry))
            return 0;
        entry.DiscardPending = true;
        return entry.Version;
    }

    public bool CompleteDiscard(Guid id, long version)
    {
        if (!_entries.TryGetValue(id, out var entry))
            return true;
        if (entry.Version == version)
        {
            _entries.Remove(id);
            changed();
            return true;
        }
        entry.DiscardPending = false;
        entry.Failure = null;
        entry.FailedWrite = null;
        entry.RecoveryRecord = null;
        entry.RecoveryDurableVersion = 0;
        changed();
        return false;
    }

    private sealed class WritePause(NoteDraftWriter owner) : IDisposable
    {
        private NoteDraftWriter? _owner = owner;

        public void Dispose()
        {
            var current = _owner;
            _owner = null;
            current?.ResumeWrites();
        }
    }

    public void AcceptConflict(Guid id, long latestRevision)
    {
        if (!_entries.TryGetValue(id, out var entry))
            return;
        entry.BaseRevision = latestRevision;
        entry.Failure = null;
        changed();
    }

    public void Retry(Guid id)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.Failure = null;
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
            || entry.FailedWrite is not null
            || entry.RecoveryRecord is not null
        )
            return false;
        _entries.Remove(id);
        changed();
        return true;
    }

    public void Reconcile(IReadOnlyList<NoteWriteAcknowledgement> acknowledgements)
    {
        foreach (var acknowledgement in acknowledgements)
        {
            var pair = _entries.FirstOrDefault(item =>
                item.Value.FailedWrite?.Draft.WriteId == acknowledgement.WriteId
            );
            if (pair.Value is not { FailedWrite: { } snapshot } entry)
                continue;
            ApplyAcknowledgement(
                pair.Key,
                entry,
                snapshot,
                acknowledgement.Note,
                acknowledgement.Recovery
            );
        }
        changed();
    }

    private void ApplyAcknowledgement(
        Guid id,
        Entry entry,
        Snapshot snapshot,
        NoteRecord? note,
        NoteRecoveryDraft? recovery
    )
    {
        entry.AcknowledgedVersion = Math.Max(entry.AcknowledgedVersion, snapshot.Version);
        entry.Failure = null;
        entry.FailedWrite = null;
        if (recovery is not null)
        {
            entry.RecoveryRecord = recovery;
            entry.RecoveryDurableVersion = snapshot.Version;
        }
        else if (note is not null)
        {
            entry.BaseRevision = note.Revision;
            entry.RecoveryRecord = null;
            entry.RecoveryDurableVersion = 0;
            if (entry.Version == snapshot.Version)
                _entries.Remove(id);
            saved(note);
        }
        acknowledged(id);
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
                if (_paused != 0 || entry.DiscardPending)
                    return;
                if (entry.RequestedVersion <= entry.AcknowledgedVersion)
                    return;
                if (entry.Version > entry.RequestedVersion)
                    return;

                var content = entry.Content;
                var snapshot = new Snapshot(
                    entry.Version,
                    new NoteDraft(
                        content.Id,
                        content.Kind,
                        content.Title,
                        content.Url,
                        content.Body,
                        entry.BaseRevision
                    ),
                    entry.RecoveryRecord
                );
                try
                {
                    if (validate(content) is not null)
                    {
                        var recovery = await storage.SaveRecoveryDraftAsync(snapshot.Draft);
                        ApplyAcknowledgement(id, entry, snapshot, null, recovery);
                    }
                    else
                    {
                        var record = snapshot.RecoveryRecord is { } recovery
                            ? await storage.SaveAndClearRecoveryAsync(snapshot.Draft, recovery)
                            : await storage.SaveAsync(snapshot.Draft);
                        ApplyAcknowledgement(id, entry, snapshot, record, null);
                    }
                    changed();
                }
                catch (Exception error)
                {
                    entry.FailedWrite = snapshot;
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

    private sealed class Entry(OwnedDraftContent content, long baseRevision, long version)
    {
        public OwnedDraftContent Content { get; set; } = content;
        public long BaseRevision { get; set; } = baseRevision;
        public long Version { get; set; } = version;
        public long RequestedVersion { get; set; }
        public long AcknowledgedVersion { get; set; }
        public long RecoveryDurableVersion { get; set; }
        public NoteRecoveryDraft? RecoveryRecord { get; set; }
        public Snapshot? FailedWrite { get; set; }
        public Exception? Failure { get; set; }
        public bool IsWriting { get; set; }
        public bool DiscardPending { get; set; }
        public Task Writer { get; set; } = Task.CompletedTask;
    }

    private sealed record Snapshot(
        long Version,
        NoteDraft Draft,
        NoteRecoveryDraft? RecoveryRecord
    );
}
