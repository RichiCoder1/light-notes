namespace LightNotes;

/// <summary>Schedules one replaceable callback on the workspace's owning reactive context.</summary>
public interface IDebounceScheduler : IDisposable
{
    /// <summary>Replaces any pending callback and invokes this one after the quiet period.</summary>
    void Restart(TimeSpan delay, Action callback);

    /// <summary>Cancels the pending callback, if any.</summary>
    void Cancel();
}
