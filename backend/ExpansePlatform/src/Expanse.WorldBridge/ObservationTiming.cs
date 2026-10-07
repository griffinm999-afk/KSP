using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Expanse.WorldBridge
{
    // Fixed-size counters: no formatting, allocation, or I/O on the observed path.
    internal sealed class ObservationTiming
    {
        private long count, total, maximum;
        internal void Record(long ticks)
        {
            ticks = Math.Max(0, ticks);
            Interlocked.Increment(ref count);
            Interlocked.Add(ref total, ticks);
            long old;
            do { old = Interlocked.Read(ref maximum); if (old >= ticks) break; }
            while (Interlocked.CompareExchange(ref maximum, ticks, old) != old);
        }
        // Counters are independent exchanges; a concurrent record may straddle
        // adjacent reporting intervals. Lifetime totals are not reset here.
        internal string Drain(string name)
        {
            long n = Interlocked.Exchange(ref count, 0), sum = Interlocked.Exchange(ref total, 0), max = Interlocked.Exchange(ref maximum, 0);
            return String.Format(CultureInfo.InvariantCulture, "{0}: count={1}, totalMs={2:F3}, maxMs={3:F3}",
                name, n, sum * 1000d / Stopwatch.Frequency, max * 1000d / Stopwatch.Frequency);
        }
    }
    internal sealed class ObservationCount
    {
        private long value;
        internal void Add(long n) { Interlocked.Add(ref value, n); }
        internal long Drain() { return Interlocked.Exchange(ref value, 0); }
    }
}
