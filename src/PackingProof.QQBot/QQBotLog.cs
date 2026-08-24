namespace PackingProof.QQBot;

internal static class QQBotLog
{
    private const int MaximumEntries = 100;
    private static readonly object Gate = new();
    private static readonly Queue<string> Entries = new();

    public static event Action<string>? Written;

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Entries.ToArray();
    }

    public static void Write(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > MaximumEntries) Entries.Dequeue();
        }
        Written?.Invoke(entry);
    }
}
