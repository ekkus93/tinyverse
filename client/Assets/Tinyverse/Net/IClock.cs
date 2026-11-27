using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary>Time source for <see cref="TinyverseConnection"/>; replaced by a manual clock in tests.</summary>
    public interface IClock
    {
        /// <summary>Monotonic milliseconds from an arbitrary origin. Used for intervals.</summary>
        long MonotonicMs { get; }

        /// <summary>Wall-clock milliseconds since the Unix epoch.</summary>
        long UnixTimeMs { get; }

        /// <summary>Completes after <paramref name="delay"/> has passed on this clock.</summary>
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>The real clock: <see cref="Stopwatch"/>, <see cref="DateTimeOffset.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        readonly Stopwatch stopwatch = Stopwatch.StartNew();

        public long MonotonicMs => stopwatch.ElapsedMilliseconds;
        public long UnixTimeMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
    }
}
