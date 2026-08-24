namespace PackingProof.QQBot;

internal sealed class QQBotSingleInstance : IDisposable
{
    private const string MutexName = "Local\\PackingProof.QQBot.Runtime.v1";
    private const string ActivationEventName = "Local\\PackingProof.QQBot.Activate.v1";
    private Mutex? _mutex;

    public EventWaitHandle ActivationEvent { get; } = new(false, EventResetMode.AutoReset, ActivationEventName);

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        return createdNew;
    }

    public static bool TryActivateExisting()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            return activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        ActivationEvent.Dispose();
    }
}
