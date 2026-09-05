using LightNotes.Storage;
using Lucent.Core;

namespace LightNotes;

/// <summary>Application-owned drafts and save coordination, independent of mounted controls.</summary>
public sealed class NoteWorkspace : IAsyncDisposable
{
    private readonly string _databasePath;
    private NoteStore? _store;
    private Task _pending = Task.CompletedTask;
    private Func<Task>? _retry;
    private Guid? _captureId;
    private bool _closing;
    private readonly Signal<bool> _needsSave;
    private readonly Signal<bool> _ready;
    private readonly Signal<bool> _busy;
    private readonly Signal<string?> _error;
    private readonly Signal<string> _status;

    public NoteWorkspace(ReactiveScope owner, string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        Capture = new(owner, "capture", name: "capture");
        Title = new(owner, "empty", name: "title");
        Url = new(owner, "empty", name: "url");
        Body = new(owner, "empty", name: "body");
        Constraints = new(owner);
        Items = owner.Signal<IReadOnlyList<NoteRecord>>([], "notes");
        Selected = owner.Signal<NoteRecord?>(null, "selected-note");
        ShowArchived = owner.Signal(false, "show-archived");
        _needsSave = owner.Signal(false, "needs-save");
        _ready = owner.Signal(false, "storage-ready");
        _busy = owner.Signal(false, "workspace-busy");
        _error = owner.Signal<string?>(null, "save-error");
        _status = owner.Signal("Opening your notes...", "save-status");
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
        Bindings = new([
            new(CaptureCommand, KeyChord.Ctrl(Key.N)),
            new(SaveCommand, KeyChord.Ctrl(Key.S)),
        ]);
    }

    public EditorSession Capture { get; }
    public EditorSession Title { get; }
    public EditorSession Url { get; }
    public EditorSession Body { get; }
    public ResponsiveConstraints Constraints { get; }
    public Signal<IReadOnlyList<NoteRecord>> Items { get; }
    public Signal<NoteRecord?> Selected { get; }
    public Signal<bool> ShowArchived { get; }
    public ApplicationCommand CaptureCommand { get; }
    public ApplicationCommand SaveCommand { get; }
    public ApplicationCommand ArchiveCommand { get; }
    public ApplicationCommand RetryCommand { get; }
    public ApplicationCommand BackupCommand { get; }
    public ApplicationCommand ToggleArchiveCommand { get; }
    public CommandBindings Bindings { get; }
    public bool IsReady => _ready.Value;
    public bool IsBusy => _busy.Value;
    public bool CanEdit => IsReady && !IsBusy && !_closing;
    public bool IsDirty =>
        Selected.Value is { } item
        && (
            _needsSave.Value
            || Title.Text != item.Title
            || Url.Text != (item.Url ?? "")
            || Body.Text != item.Body
        );
    public string StatusText =>
        _error.Value is { } error ? "Could not save. " + error + " Retry to keep your changes."
        : IsBusy ? _status.Value
        : IsDirty ? "Unsaved changes - Ctrl+S to save"
        : _status.Value;

    public Task StartAsync() => Run(LoadAsync, "Opening your notes...");

    public void Select(Guid id)
    {
        if (!CanEdit || Selected.Value?.Id == id)
            return;
        _ = Run(
            async () =>
            {
                await SaveCurrentAsync();
                SelectRecord(Items.Value.FirstOrDefault(item => item.Id == id));
            },
            "Opening note..."
        );
    }

    private Task Run(Func<Task> action, string status)
    {
        if (IsBusy)
            return _pending;
        _pending = RunCore(action, status);
        return _pending;
    }

    private async Task RunCore(Func<Task> action, string status)
    {
        _busy.Value = true;
        _error.Value = null;
        _status.Value = status;
        try
        {
            await action();
            _retry = null;
            if (_status.Value == status)
                _status.Value = "Saved on this device";
        }
        catch (Exception error)
        {
            if (action != RetryAsync)
                _retry = action;
            _error.Value = error.Message;
            if (
                error is NoteConcurrencyException conflict
                && _store is not null
                && Selected.Value?.Id == conflict.NoteId
            )
            {
                try
                {
                    if (await _store.GetAsync(conflict.NoteId) is { } latest)
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
        }
        finally
        {
            _busy.Value = false;
        }
    }

    private async Task LoadAsync()
    {
        _store ??= await NoteStore.OpenAsync(_databasePath);
        await RefreshAsync();
        SelectRecord((Items.Value.Count == 0 ? null : Items.Value[0]));
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
    }

    private async Task SaveCurrentAsync()
    {
        if (Selected.Value is not { } item || !IsDirty)
            return;
        if (string.IsNullOrWhiteSpace(Title.Text))
            throw new ArgumentException("Give this note a title before saving.");
        if (
            !string.IsNullOrWhiteSpace(Url.Text)
            && (
                !Uri.TryCreate(Url.Text, UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https")
            )
        )
            throw new ArgumentException(
                "Use a complete http or https link, or leave the URL empty."
            );
        var draft = new NoteDraft(
            item.Id,
            string.IsNullOrWhiteSpace(Url.Text) ? NoteKind.Note : NoteKind.Link,
            Title.Text,
            string.IsNullOrWhiteSpace(Url.Text) ? null : Url.Text,
            Body.Text,
            item.Revision
        );
        var saved = await Store.SaveAsync(draft);
        Selected.Value = saved;
        _needsSave.Value = false;
        await RefreshAsync();
    }

    private async Task ArchiveAsync()
    {
        await SaveCurrentAsync();
        if (Selected.Value is not { } item)
            return;
        await Store.ArchiveAsync(item.Id, !item.IsArchived);
        await RefreshAsync();
        SelectRecord((Items.Value.Count == 0 ? null : Items.Value[0]));
    }

    private async Task ToggleArchiveAsync()
    {
        await SaveCurrentAsync();
        ShowArchived.Value = !ShowArchived.Value;
        await RefreshAsync();
        SelectRecord((Items.Value.Count == 0 ? null : Items.Value[0]));
    }

    private async Task RefreshAsync()
    {
        var all = await Store.ListAsync(includeArchived: true);
        Items.Value = all.Where(item => item.IsArchived == ShowArchived.Value).ToArray();
    }

    private void SelectRecord(NoteRecord? item)
    {
        _needsSave.Value = false;
        Selected.Value = item;
        var id = item?.Id.ToString() ?? "empty";
        Title.SwitchDocument(id, item?.Title ?? "");
        Url.SwitchDocument(id, item?.Url ?? "");
        Body.SwitchDocument(id, item?.Body ?? "");
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
                    Selected.Value = captured;
                }
            }
        }
        else if (_retry is { } retry)
            await retry();
        else
            await LoadAsync();
        if (_store is not null)
        {
            await RefreshAsync();
            SelectRecord(
                Items.Value.FirstOrDefault(item => item.Id == Selected.Value?.Id)
                    ?? (Items.Value.Count == 0 ? null : Items.Value[0])
            );
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
        await _pending;
        if (_store is null)
            return true;
        if (
            _error.Value is not null
            && (IsDirty || _store.HasUnresolvedWriteFailures || _captureId is not null)
        )
        {
            _closing = false;
            return false;
        }
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
            _error.Value = "Some accepted changes are not yet saved.";
            _retry = SaveCurrentAsync;
        }
        catch (Exception error)
        {
            _error.Value = error.Message;
            _retry = SaveCurrentAsync;
        }
        _closing = false;
        return false;
    }

    private NoteStore Store =>
        _store ?? throw new InvalidOperationException("Your notes are still opening.");

    public async ValueTask DisposeAsync()
    {
        await _pending;
        if (_store is not null)
            await _store.DisposeAsync();
    }
}
