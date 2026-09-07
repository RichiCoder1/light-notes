namespace LightNotes.Storage;

public enum NoteKind
{
    Note = 0,
    Link = 1,
}

public sealed record NoteDraft(
    Guid Id,
    NoteKind Kind,
    string Title,
    string? Url,
    string Body,
    long? ExpectedRevision = null
);

public sealed record NoteRecord(
    Guid Id,
    NoteKind Kind,
    string Title,
    string? Url,
    string Body,
    bool IsArchived,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>A recoverable editor snapshot kept separately from the last valid note.</summary>
public sealed record NoteRecoveryDraft(
    Guid Id,
    NoteKind Kind,
    string Title,
    string? Url,
    string Body,
    long BaseRevision,
    DateTimeOffset UpdatedAt
);

public sealed record WriteRetryResult(int Retried, int Succeeded, int Remaining);

public sealed class NoteConcurrencyException : InvalidOperationException
{
    public NoteConcurrencyException(Guid id, long expectedRevision)
        : base($"Note '{id}' does not have expected revision {expectedRevision}.")
    {
        NoteId = id;
        ExpectedRevision = expectedRevision;
    }

    public Guid NoteId { get; }

    public long ExpectedRevision { get; }
}

public sealed class UnsupportedSchemaVersionException : InvalidOperationException
{
    public UnsupportedSchemaVersionException(int actualVersion, int supportedVersion)
        : base(
            $"Database schema version {actualVersion} is not supported; this application supports version {supportedVersion}."
        )
    {
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
    }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }
}

public sealed class NoteStoreClosedException : InvalidOperationException
{
    public NoteStoreClosedException()
        : base("The note store is closing or closed and cannot accept more work.") { }
}
