using Lucent.Core;
using Lucent.Reactive.R3;

namespace LightNotes;

/// <summary>Uses Lucent's owned R3 debounce while preserving the workspace's testable scheduling seam.</summary>
internal sealed class R3DebounceScheduler(ReactiveScope owner, TimeProvider timeProvider)
    : IDebounceScheduler
{
    private readonly OwnedDebouncedAction _action = new(owner, timeProvider);

    public void Restart(TimeSpan delay, Action callback) => _action.Restart(delay, callback);

    public void Cancel() => _action.Cancel();

    public void Dispose() => _action.Dispose();
}
