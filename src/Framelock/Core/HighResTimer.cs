using System.Diagnostics;

namespace Framelock.Core;

/// <summary>
/// Sub-millisecond frame pacing: sleeps on a high-resolution waitable timer, then spins for the final few hundred microseconds.
/// </summary>
public sealed class HighResTimer : IDisposable
{
    private readonly IntPtr _timer;
    private static readonly long SpinTicks = Stopwatch.Frequency / 2000; // 0.5 ms

    public HighResTimer()
    {
        _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero) // pre-1803 fallback
            _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, Native.TIMER_ALL_ACCESS);
    }

    /// <summary>Blocks until <paramref name="targetTimestamp"/> (Stopwatch ticks).</summary>
    public void WaitUntil(long targetTimestamp)
    {
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            long remaining = targetTimestamp - now;
            if (remaining <= 0) return;
            if (remaining > SpinTicks && _timer != IntPtr.Zero)
            {
                long sleepTicks = remaining - SpinTicks;
                long due100ns = -(sleepTicks * 10_000_000 / Stopwatch.Frequency); // relative
                if (due100ns >= 0) due100ns = -1;
                if (Native.SetWaitableTimerEx(_timer, ref due100ns, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    Native.WaitForSingleObject(_timer, 1000);
                    continue;
                }
            }
            if (remaining > SpinTicks * 4) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) Native.CloseHandle(_timer);
    }
}
