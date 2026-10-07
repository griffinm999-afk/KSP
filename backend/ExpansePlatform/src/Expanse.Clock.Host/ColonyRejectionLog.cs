namespace Expanse.Clock.Host;

// Bounded metadata only. Never log payloads, exception messages, or private values here.
internal static class ColonyRejectionLog
{
    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> Last = new(StringComparer.Ordinal);
    internal static void Write(string diagnostic)
    {
        lock (Gate)
        {
            var key = Last.ContainsKey(diagnostic) || Last.Count < 7 ? diagnostic : "other colony validation failure";
            var now = DateTime.UtcNow;
            if (Last.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(60)) return;
            Last[key] = now;
            Console.Error.WriteLine("Colony validation rejected: " + key);
        }
    }
}
