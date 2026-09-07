using LightNotes.Storage;
using Lucent.Core;

namespace LightNotes;

/// <summary>Application-owned drafts and save coordination, independent of mounted controls.</summary>
public sealed class NoteWorkspace : IAsyncDisposable
{
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(750);
    private readonly string _databasePath;
    private readonly IExternalLinkOpener _linkOpener;
    private readonly IDebounceScheduler _autosave;
    private readonly Func<string, Task<INoteWorkspaceStorage>> _openStore;
    private INoteWorkspaceStorage? _store;
    private Task _pending = Task.CompletedTask;
    private Task _saveLoop = Task.CompletedTask;
    private Func<Task>? _retry;
    private Guid? _captureId;
    private bool _closing;
    private long _draftVersion;
    private long _savedDraftVersion;
    private long _requestedSaveVersion;
    private DraftContent? _observedDraft;
    private DraftSnapshot? _latestDraft;
    private Exception? _saveFailure;
    private readonly Signal<bool> _needsSave;
    private readonly Signal<bool> _ready;
    private readonly Signal<bool> _busy;
    private readonly Signal<bool> _saving;
    private readonly Signal<string?> _error;
    private readonly Signal<string> _errorHeading;
    private readonly Signal<string> _status;
    private readonly Signal<IReadOnlyList<NoteRecord>> _allItems;
    private readonly Signal<NoteWorkspaceRoute> _route;

    public NoteWorkspace(
        ReactiveScope owner,
        string databasePath,
        IExternalLinkOpener linkOpener,
        IDebounceScheduler autosave
    )
        : this(
            owner,
            databasePath,
            linkOpener,
            autosave,
            async path => new NoteWorkspaceStorage(await NoteStore.OpenAsync(path))
        ) { }

    internal NoteWorkspace(
        ReactiveScope owner,
        string databasePath,
        IExternalLinkOpener linkOpener,
        IDebounceScheduler autosave,
        Func<string, Task<INoteWorkspaceStorage>> openStore
    )
    {
        _databasePath = Path.GetFullPath(databasePath);
        _linkOpener = linkOpener ?? throw new ArgumentNullException(nameof(linkOpener));
        _autosave = autosave ?? throw new ArgumentNullException(nameof(autosave));
        _openStore = openStore ?? throw new ArgumentNullException(nameof(openStore));
        Capture = new(owner, "capture", name: "capture");
        Title = new(owner, "empty", name: "title");
        Url = new(owner, "empty", name: "url");
        Body = new(owner, "empty", name: "body", multiline: true);
        Search = new(owner, "search", name: "search");
        CaptureFocus = new(owner, "capture-focus");
        SearchFocus = new(owner, "search-focus");
        TitleFocus = new(owner, "title-focus");
        Constraints = new(owner);
        Items = owner.Signal<IReadOnlyList<NoteRecord>>([], "notes");
        _allItems = owner.Signal<IReadOnlyList<NoteRecord>>([], "all-notes");
        Selected = owner.Signal<NoteRecord?>(null, "selected-note");
        ShowArchived = owner.Signal(false, "show-archived");
        _route = owner.Signal(NoteWorkspaceRoute.Collection, "route");
        _ = owner.Effect(ApplyFilter, "notes-filter");
        _needsSave = owner.Signal(false, "needs-save");
        _ready = owner.Signal(false, "storage-ready");
        _busy = owner.Signal(false, "workspace-busy");
        _saving = owner.Signal(false, "workspace-saving");
        _error = owner.Signal<string?>(null, "save-error");
        _errorHeading = owner.Signal("Could not save", "error-heading");
        _status = owner.Signal("Opening your notes...", "save-status");
        _ = owner.Effect(ObserveDraft, "draft-autosave");
        CaptureCommand = new(
            owner,
            _ => Run(CaptureAsync, "Saving..."),
            () => CanEdit && !string.IsNullOrWhiteSpace(Capture.Text),
            "capture-note"
        );
        SaveCommand = new(
            owner,
            _ => Run(SaveCurrentAsync, "Saving..."),
            () => CanEdit && Selected.Value is not null && IsDirty,
            "save-note"
        );
        ArchiveCommand = new(
            owner,
            _ => Run(ArchiveAsync, "Saving..."),
            () => CanEdit && Selected.Value is not null,
            "archive-note"
        );
        OpenLinkCommand = new(
            owner,
            _ => Run(OpenLinkAsync, "Opening link...", "Could not open link"),
            () => CanEdit && CanOpenLink,
            "open-link"
        );
        RetryCommand = new(
            owner,
            _ => Run(RetryAsync, "Trying again..."),
            () => !IsBusy && _error.Value is not null,
            "retry-storage"
        );
        BackupCommand = new(
            owner,
            _ => Run(BackupAsync, "Creating backup..."),
            () => CanEdit,
            "backup-notes"
        );
        ToggleArchiveCommand = new(
            owner,
            _ => Run(ToggleArchiveAsync, "Opening notes..."),
            () => CanEdit,
            "toggle-archive"
        );
        FocusCaptureCommand = new(
            owner,
            _ =>
            {
                RequestFocus(CaptureFocus, selectAll: true);
                return Task.CompletedTask;
            },
            () => CanEdit,
            "focus-capture"
        );
        FocusSearchCommand = new(
            owner,
            _ =>
            {
                if (IsCompact)
                    _route.Value = NoteWorkspaceRoute.Collection;
                RequestFocus(SearchFocus, selectAll: true);
                return Task.CompletedTask;
            },
            () => CanEdit,
            "focus-search"
        );
        Bindings = new([
            new(FocusCaptureCommand, KeyChord.Ctrl(Key.N)),
            new(FocusSearchCommand, KeyChord.Ctrl(Key.F)),
            new(SaveCommand, KeyChord.Ctrl(Key.S)),
        ]);
    }

    public EditorSession Capture { get; }
    public EditorSession Title { get; }
    public EditorSession Url { get; }
    public EditorSession Body { get; }
    public EditorSession Search { get; }
    public FocusTarget CaptureFocus { get; }
    public FocusTarget SearchFocus { get; }
    public FocusTarget TitleFocus { get; }
    public ApplicationCommand FocusCaptureCommand { get; }
    public ApplicationCommand FocusSearchCommand { get; }
    public ResponsiveConstraints Constraints { get; }
    public Signal<IReadOnlyList<NoteRecord>> Items { get; }

    /// <summary>Gets the currently visible, archive-filtered and query-filtered records.</summary>
    public IReadOnlyList<NoteRecord> VisibleItems => Items.Value;
    public Signal<NoteRecord?> Selected { get; }
    public Signal<bool> ShowArchived { get; }
    public Signal<NoteWorkspaceRoute> Route => _route;
    public ApplicationCommand CaptureCommand { get; }
    public ApplicationCommand SaveCommand { get; }
    public ApplicationCommand ArchiveCommand { get; }
    public ApplicationCommand OpenLinkCommand { get; }
    public ApplicationCommand RetryCommand { get; }
    public ApplicationCommand BackupCommand { get; }
    public ApplicationCommand ToggleArchiveCommand { get; }
    public CommandBindings Bindings { get; }
    public bool IsReady => _ready.Value;
    public bool IsBusy => _busy.Value;
    public bool IsSaving => _saving.Value;
    public bool CanEdit => IsReady && !IsBusy && !_closing;
    public bool CanSearch => IsReady && !_closing;
    public bool CanOpenLink => TryGetSelectedWebUri(out _);
    public bool IsDirty
    {
        get
        {
            var selected = Selected.Value;
            var title = Title.Text;
            var url = Url.Text;
            var body = Body.Text;
            return selected is { } item
                && (
                    _needsSave.Value
                    || _latestDraft is { } latest && latest.Version > _savedDraftVersion
                    || title != item.Title
                    || url != (item.Url ?? "")
                    || body != item.Body
                );
        }
    }
    public string StatusText =>
        _error.Value is { } error ? _errorHeading.Value + ". " + error
        : IsBusy ? _status.Value
        : IsSaving ? "Saving..."
        : IsDirty ? "Saving changes..."
        : _status.Value;

    /// <summary>Gets whether the store is still loading and has not reported an error.</summary>
    public bool IsLoading => !IsReady && !HasError;

    /// <summary>Gets whether the most recent store operation failed.</summary>
    public bool HasError => _error.Value is not null;

    /// <summary>Gets the store failure text, when one is available.</summary>
    public string? ErrorMessage => _error.Value;

    /// <summary>Gets a short semantic heading for the current operation failure.</summary>
    public string ErrorHeading => _errorHeading.Value;

    /// <summary>Gets the current plain-text query mirrored by the Search editor session.</summary>
    public string Query => Search.Text;

    /// <summary>Gets the active shell width bucket from the mounted responsive container.</summary>
    public NoteWorkspaceLayout Layout
    {
        get
        {
            var width = Constraints.Current.Width;
            return width >= 1060f ? NoteWorkspaceLayout.Wide
                : width >= 840f ? NoteWorkspaceLayout.Medium
                : NoteWorkspaceLayout.Compact;
        }
    }

    public bool IsWide => Layout == NoteWorkspaceLayout.Wide;
    public bool IsCompact => Layout == NoteWorkspaceLayout.Compact;
    public bool ShowCollection => !IsCompact || _route.Value == NoteWorkspaceRoute.Collection;
    public bool ShowEditor => !IsCompact || _route.Value == NoteWorkspaceRoute.Editor;
    public bool IsFiltering => !string.IsNullOrWhiteSpace(Search.Text);
    public bool HasItems => VisibleItems.Count != 0;
    public bool HasSearchResults => HasItems;
    public string EmptyStateText =>
        IsFiltering ? $"No notes match \"{Search.Text.Trim()}\"."
        : ShowArchived.Value ? "Nothing is archived yet."
        : "Your inbox is clear.";
    public string CollectionTitle => ShowArchived.Value ? "Archive" : "Inbox";
    public int InboxCount => _allItems.Value.Count(item => !item.IsArchived);
    public int ArchiveCount => _allItems.Value.Count(item => item.IsArchived);

    public Task StartAsync() => Run(LoadAsync, "Opening your notes...", "Could not open notes");

    /// <summary>Returns the compact shell to its collection route while retaining the editor draft.</summary>
    public void BackToCollection()
    {
        if (!IsCompact)
            return;
        _route.Value = NoteWorkspaceRoute.Collection;
        RequestFocus(SearchFocus);
    }

    private void RequestFocus(FocusTarget target, bool selectAll = false)
    {
        CaptureFocus.Cancel();
        SearchFocus.Cancel();
        TitleFocus.Cancel();
        target.Request(selectAll);
    }

    /// <summary>Shows the inbox, saving the active draft before refreshing the collection.</summary>
    public void ShowInbox() => SelectCollection(false);

    /// <summary>Shows the archive, saving the active draft before refreshing the collection.</summary>
    public void ShowArchive() => SelectCollection(true);

    public void Select(Guid id)
    {
        if (!CanEdit)
            return;
        if (Selected.Value?.Id == id)
        {
            if (IsCompact)
            {
                _route.Value = NoteWorkspaceRoute.Editor;
                RequestFocus(TitleFocus);
            }
            return;
        }
        _ = Run(
            async () =>
            {
                await SaveCurrentAsync();
                var selected = Items.Value.FirstOrDefault(item => item.Id == id);
                SelectRecord(selected);
                if (selected is not null && IsCompact)
                {
                    _route.Value = NoteWorkspaceRoute.Editor;
                    RequestFocus(TitleFocus);
                }
            },
            "Opening note..."
        );
    }

    private void SelectCollection(bool archived)
    {
        if (!CanEdit)
            return;
        if (ShowArchived.Value == archived)
        {
            BackToCollection();
            return;
        }
        _ = Run(() => SetCollectionAsync(archived), "Opening notes...");
    }

    private async Task SetCollectionAsync(bool archived)
    {
        await SaveCurrentAsync();
        ShowArchived.Value = archived;
        await RefreshAsync();
        SelectRecord(VisibleItems.Count == 0 ? null : VisibleItems[0]);
        BackToCollection();
    }

    private Task Run(Func<Task> action, string status, string errorHeading = "Could not save")
    {
        if (IsBusy)
            return _pending;
        _pending = RunCore(action, status, errorHeading);
        return _pending;
    }

    private async Task RunCore(Func<Task> action, string status, string errorHeading)
    {
        _busy.Value = true;
        var preservingWriteFailure = HasPendingWriteFailure;
        if (!preservingWriteFailure)
            _error.Value = null;
        _status.Value = status;
        try
        {
            await action();
            if (!HasPendingWriteFailure)
            {
                _retry = null;
                _error.Value = null;
            }
            if (_status.Value == status)
                _status.Value = "Saved on this device";
        }
        catch (Exception error)
        {
            if (action != RetryAsync && !preservingWriteFailure)
                _retry = action;
            if (!preservingWriteFailure && !ReferenceEquals(error, _saveFailure))
            {
                _error.Value = error.Message;
                _errorHeading.Value = errorHeading;
            }
        }
        finally
        {
            _busy.Value = false;
        }
    }

    private async Task LoadAsync()
    {
        _store ??= await _openStore(_databasePath);
        await RefreshAsync();
        SelectRecord((VisibleItems.Count == 0 ? null : VisibleItems[0]));
        _route.Value = NoteWorkspaceRoute.Collection;
        _ready.Value = true;
        _status.Value =
            Items.Value.Count == 0 ? "Your notes stay on this device" : "Saved on this device";
    }

    private async Task CaptureAsync()
    {
        await SaveCurrentAsync();
        var text = Capture.Text.Trim();
        if (text.Length == 0)
            return;
        var isLink =
            Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https");
        var draft = new NoteDraft(
            _captureId ??= Guid.NewGuid(),
            isLink ? NoteKind.Link : NoteKind.Note,
            isLink ? uri!.Host : text,
            isLink ? text : null,
            isLink ? "" : text,
            0
        );
        var saved = await Store.SaveAsync(draft);
        _captureId = null;
        Capture.Text = "";
        ShowArchived.Value = false;
        await RefreshAsync();
        SelectRecord(saved);
        if (IsCompact)
        {
            _route.Value = NoteWorkspaceRoute.Editor;
            RequestFocus(TitleFocus);
        }
    }

    private void ObserveDraft()
    {
        var selected = Selected.Value;
        if (!_ready.Value || selected is null)
            return;

        var content = CurrentDraft(selected.Id);
        if (_observedDraft == content)
            return;

        var hadPendingSave =
            !_saveLoop.IsCompleted
            || _savedDraftVersion < _requestedSaveVersion
            || HasPendingWriteFailure;
        _observedDraft = content;
        _draftVersion = checked(_draftVersion + 1);
        _latestDraft = new(_draftVersion, content);
        if (Matches(content, selected) && !_needsSave.Value && !hadPendingSave)
        {
            _autosave.Cancel();
            _savedDraftVersion = _draftVersion;
            _requestedSaveVersion = _draftVersion;
            return;
        }

        var scheduledVersion = _draftVersion;
        _autosave.Restart(AutosaveDelay, () => QueueAutosave(scheduledVersion));
    }

    private DraftContent CurrentDraft(Guid id)
    {
        var url = string.IsNullOrWhiteSpace(Url.Text) ? null : Url.Text;
        return new(id, url is null ? NoteKind.Note : NoteKind.Link, Title.Text, url, Body.Text);
    }

    private static bool Matches(DraftContent draft, NoteRecord record) =>
        draft.Id == record.Id
        && draft.Kind == record.Kind
        && draft.Title == record.Title
        && draft.Url == record.Url
        && draft.Body == record.Body;

    private void QueueAutosave(long version)
    {
        if (_closing || _latestDraft is not { } latest || latest.Version != version)
            return;
        RequestSave(version);
    }

    private void RequestSave(long version)
    {
        _requestedSaveVersion = Math.Max(_requestedSaveVersion, version);
        if (_saveLoop.IsCompleted)
            _saveLoop = SaveLoopAsync();
    }

    private async Task SaveLoopAsync()
    {
        _saving.Value = true;
        try
        {
            while (_savedDraftVersion < _requestedSaveVersion)
            {
                if (_latestDraft is not { } snapshot)
                    return;
                if (snapshot.Version > _requestedSaveVersion)
                    return;
                if (Selected.Value is not { } selected || selected.Id != snapshot.Content.Id)
                    return;

                NoteRecord saved;
                try
                {
                    saved = await Store.SaveAsync(ToDraft(snapshot.Content, selected.Revision));
                }
                catch (Exception error)
                {
                    await RecordSaveFailureAsync(error, snapshot.Content.Id);
                    return;
                }

                _savedDraftVersion = Math.Max(_savedDraftVersion, snapshot.Version);
                _saveFailure = null;
                _error.Value = null;
                if (Selected.Value?.Id == saved.Id)
                    Selected.Value = saved;
                ReplaceRecord(saved);
                if (_latestDraft is { } latest && latest.Version == snapshot.Version)
                {
                    _needsSave.Value = false;
                    _status.Value = "Saved on this device";
                }
            }
        }
        finally
        {
            _saving.Value = false;
        }
    }

    private bool HasPendingWriteFailure =>
        _saveFailure is not null || (_store?.HasUnresolvedWriteFailures ?? false);

    private static NoteDraft ToDraft(DraftContent content, long expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(content.Title))
            throw new ArgumentException("Give this note a title before saving.");
        if (
            content.Url is { } value
            && (
                !Uri.TryCreate(value, UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https")
            )
        )
            throw new ArgumentException(
                "Use a complete http or https link, or leave the URL empty."
            );
        return new(
            content.Id,
            content.Kind,
            content.Title,
            content.Url,
            content.Body,
            expectedRevision
        );
    }

    private async Task RecordSaveFailureAsync(Exception error, Guid noteId)
    {
        _saveFailure = error;
        _errorHeading.Value = "Could not save";
        _error.Value = error.Message;
        _retry = SaveCurrentAsync;
        if (error is not NoteConcurrencyException conflict || conflict.NoteId != noteId)
            return;
        try
        {
            if (await Store.GetAsync(noteId) is { } latest && Selected.Value?.Id == noteId)
            {
                Selected.Value = latest;
                _needsSave.Value = true;
                _error.Value =
                    "This note changed on disk. Your draft is kept; Retry saves it over the newer version.";
            }
        }
        catch
        { /* Keep the original failure and draft when refresh also fails. */
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (Selected.Value is not { } selected || !IsDirty)
            return;

        ObserveDraft();
        _autosave.Cancel();
        var target = _latestDraft?.Version ?? _draftVersion;
        RequestSave(target);
        await _saveLoop;
        if (_savedDraftVersion < target && _saveFailure is { } failure)
            throw failure;
    }

    private void ReplaceRecord(NoteRecord saved)
    {
        _allItems.Value = _allItems
            .Value.Where(item => item.Id != saved.Id)
            .Append(saved)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .ToArray();
        ApplyFilter();
    }

    private async Task ArchiveAsync()
    {
        await SaveCurrentAsync();
        if (Selected.Value is not { } item)
            return;
        await Store.ArchiveAsync(item.Id, !item.IsArchived);
        await RefreshAsync();
        SelectRecord(VisibleItems.Count == 0 ? null : VisibleItems[0]);
        BackToCollection();
    }

    private Task ToggleArchiveAsync() => SetCollectionAsync(!ShowArchived.Value);

    private async Task OpenLinkAsync()
    {
        if (!TryGetSelectedWebUri(out var uri))
            throw new InvalidOperationException(
                "The selected note does not have a valid web link."
            );
        await _linkOpener.OpenAsync(uri);
        _status.Value = "Opened link";
    }

    private bool TryGetSelectedWebUri(out Uri uri)
    {
        if (
            Selected.Value is not null
            && Url.Text is { } value
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
        )
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private async Task RefreshAsync()
    {
        var all = await Store.ListAsync(includeArchived: true);
        _allItems.Value = all;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = Search.Text.Trim();
        var archived = ShowArchived.Value;
        var visible = _allItems
            .Value.Where(item => item.IsArchived == archived)
            .Where(item =>
                query.Length == 0
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Url?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || item.Body.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        Items.Value = visible;
    }

    private void SelectRecord(NoteRecord? item)
    {
        _autosave.Cancel();
        _needsSave.Value = false;
        Selected.Value = item;
        var id = item?.Id.ToString() ?? "empty";
        Title.SwitchDocument(id, item?.Title ?? "");
        Url.SwitchDocument(id, item?.Url ?? "");
        Body.SwitchDocument(id, item?.Body ?? "");
        _draftVersion = checked(_draftVersion + 1);
        _observedDraft = item is null
            ? null
            : new(item.Id, item.Kind, item.Title, item.Url, item.Body);
        _latestDraft = _observedDraft is { } content ? new(_draftVersion, content) : null;
        _savedDraftVersion = _draftVersion;
        _requestedSaveVersion = _draftVersion;
        _saveFailure = null;
    }

    private async Task RetryAsync()
    {
        if (_store is { HasUnresolvedWriteFailures: true })
        {
            await SaveCurrentAsync();
            var result = await _store.RetryFailedWritesAsync();
            if (result.Remaining != 0)
                throw new IOException("Accepted changes still could not be saved.");
            if (_captureId is { } pendingId)
            {
                var captured = await _store.GetAsync(pendingId);
                if (captured is not null)
                {
                    _captureId = null;
                    var capturedText =
                        captured.Kind == NoteKind.Link ? captured.Url : captured.Body;
                    if (Capture.Text.Trim() == capturedText)
                        Capture.Text = "";
                    SelectRecord(captured);
                }
            }
        }
        else if (_retry is { } retry)
            await retry();
        else
            await LoadAsync();
        if (_store is not null)
        {
            var selectedId = Selected.Value?.Id;
            await RefreshAsync();
            var refreshed = Items.Value.FirstOrDefault(item => item.Id == selectedId);
            if (refreshed is not null)
                Selected.Value = refreshed;
            else
                SelectRecord(Items.Value.Count == 0 ? null : Items.Value[0]);
        }
    }

    private async Task BackupAsync()
    {
        await SaveCurrentAsync();
        var destination = Path.Combine(
            Path.GetDirectoryName(_databasePath)!,
            "Backups",
            $"notes-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"
        );
        await Store.BackupAsync(destination);
        _status.Value = "Backup created in your Light Notes data folder";
    }

    public async ValueTask<bool> PrepareCloseAsync()
    {
        _closing = true;
        _autosave.Cancel();
        await _pending;
        if (_store is null)
            return true;
        await Run(SaveCurrentAsync, "Finishing your save...");
        if (_error.Value is not null)
        {
            _closing = false;
            return false;
        }
        try
        {
            if (await _store.PrepareCloseAsync())
                return true;
            _errorHeading.Value = "Could not save";
            _error.Value = "Some accepted changes are not yet saved.";
            _retry = SaveCurrentAsync;
        }
        catch (Exception error)
        {
            _errorHeading.Value = "Could not save";
            _error.Value = error.Message;
            _retry = SaveCurrentAsync;
        }
        _closing = false;
        return false;
    }

    private INoteWorkspaceStorage Store =>
        _store ?? throw new InvalidOperationException("Your notes are still opening.");

    public async ValueTask DisposeAsync()
    {
        _autosave.Cancel();
        await _pending;
        await _saveLoop;
        try
        {
            if (_store is not null)
                await _store.DisposeAsync();
        }
        finally
        {
            _autosave.Dispose();
        }
    }

    private readonly record struct DraftContent(
        Guid Id,
        NoteKind Kind,
        string Title,
        string? Url,
        string Body
    );

    private readonly record struct DraftSnapshot(long Version, DraftContent Content);
}
