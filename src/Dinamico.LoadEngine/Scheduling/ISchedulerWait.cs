using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dinamico.LoadEngine.Scheduling;

/// <summary>Uma espera por despertar, substituível por tempo simulado; nunca por request.</summary>
public interface ISchedulerWait
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SchedulerWait : ISchedulerWait
{
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        // Task.Delay tem um teto por chamada. A agenda relê o relógio após cada espera;
        // dividir uma espera longa não altera a origem nem acumula drift.
        var boundedDelay = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            await DelayWindowsAsync(boundedDelay, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess && boundedDelay <= TimeSpan.FromMilliseconds(10))
        {
            await DelayLinuxAsync(boundedDelay, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Task.Delay quantiza em milissegundos: arredondar para cima evita uma
        // sequência de esperas efetivamente zero junto à borda do segmento.
        long milliseconds = boundedDelay.Ticks / TimeSpan.TicksPerMillisecond
            + (boundedDelay.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
        boundedDelay = TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));
        await Task.Delay(boundedDelay, cancellationToken).ConfigureAwait(false);
    }

    // Linux managed timers can wake on a coarser grid than the 1 ms arrival quantum.
    // A short monotonic native sleep avoids that quantization without spinning or changing
    // process timer slack. The serial scheduler queues one wait at a time, never per request.
    // Cancellation stops observing immediately; an already-running syscall may finish later.
    [SupportedOSPlatform("linux")]
    private static async Task DelayLinuxAsync(TimeSpan delay, CancellationToken token)
    {
        var requested = new LinuxTimespec(checked(delay.Ticks * 100));
        Task<int> pending = Task.Run(() => ClockNanosleep(1, 0, in requested, 0), token);
        int error;
        try { error = await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // Observe even linkage failures after cancellation; no native handles are retained.
            _ = pending.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        // EINTR is an early wake: the scheduler re-reads its absolute clock before offering.
        // clock_nanosleep returns errno directly, independently of the thread's errno slot.
        if (error is not (0 or 4)) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LinuxTimespec(long nanoseconds)
    {
        public readonly long Seconds = 0;
        public readonly long Nanoseconds = nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_nanosleep", ExactSpelling = true)]
    private static extern int ClockNanosleep(int clock, int flags, in LinuxTimespec requested, nint remaining);

    // Task.Delay on Windows follows the system timer resolution, which can exceed
    // the scheduler's 5 ms tolerance even on an idle machine. One high-resolution
    // waitable timer per wake avoids changing global timer resolution or busy-spinning.
    [SupportedOSPlatform("windows")]
    private static async Task DelayWindowsAsync(TimeSpan delay, CancellationToken token)
    {
        using var timer = new TimerWaitHandle();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long due = -delay.Ticks; // Relative, in 100 ns units; no wall-clock dependency.
        if (!SetWaitableTimerEx(timer.SafeWaitHandle, in due, 0, 0, 0, 0, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var registration = ThreadPool.RegisterWaitForSingleObject(timer,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), ready,
            Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await ready.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // Callback only touches its completion source; it never accesses the handle.
            registration.Unregister(null);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class TimerWaitHandle : WaitHandle
    {
        internal TimerWaitHandle()
        {
            var handle = CreateWaitableTimerExW(0, 0, 0x2, 0x00100002); // HIGH_RESOLUTION; SYNCHRONIZE | TIMER_MODIFY_STATE
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            SafeWaitHandle = handle;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, in long dueTime, int period,
        nint callback, nint argument, nint wakeContext, uint tolerableDelay);
}
