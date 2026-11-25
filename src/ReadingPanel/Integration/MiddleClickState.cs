namespace ReadingPanel.Integration;

// The mouse hook reads synchronously. Each connected browser waits for its next settings update.
internal sealed class MiddleClickState(bool initial)
{
    private readonly object sync = new();
    private volatile bool enabled = initial;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Enabled
    {
        get => enabled;
        set
        {
            lock (sync)
            {
                if (enabled == value) return;
                enabled = value;
                changed.SetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public (bool Enabled, Task Changed) Observe()
    {
        lock (sync) return (enabled, changed.Task);
    }
}
