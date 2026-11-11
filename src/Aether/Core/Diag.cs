using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Aether.Core;

/// <summary>
/// Cheap call counters for finding what keeps the app busy. Only active when the
/// environment variable AETHER_DIAG=1; then a summary is written to error.log every 5 s.
/// </summary>
internal static class Diag
{
    public static readonly bool On = Environment.GetEnvironmentVariable("AETHER_DIAG") == "1";
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static Timer? _timer;

    public static void Hit(string name)
    {
        if (!On) return;
        Counts.AddOrUpdate(name, 1, (_, v) => v + 1);
        _timer ??= new Timer(_ => Flush(), null, 5000, 5000);
    }

    private static void Flush()
    {
        var snapshot = Counts.ToArray();
        Counts.Clear();
        if (snapshot.Length == 0) return;
        Log.Info("[diag] " + string.Join("  ", snapshot.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
