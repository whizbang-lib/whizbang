using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Drives a <see cref="FakeTimeProvider"/> forward until something the test is waiting for happens.
/// </summary>
/// <remarks>
/// A worker that backs off waits on its injected clock, so a test that wants the backoff to elapse
/// advances that clock rather than waiting out real time. The awaited task carries its own timeout,
/// so a wait that never completes fails the test instead of stepping forever.
/// </remarks>
internal static class FakeClockPump {
  /// <summary>Advances <paramref name="time"/> in steps until <paramref name="until"/> completes.</summary>
  internal static async Task StepUntilAsync(FakeTimeProvider time, Task until, TimeSpan? step = null) {
    ArgumentNullException.ThrowIfNull(time);
    ArgumentNullException.ThrowIfNull(until);
    var increment = step ?? TimeSpan.FromMilliseconds(50);
    while (!until.IsCompleted) {
      time.Advance(increment);
      await Task.Yield();
    }

    await until;
  }
}
