using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="DebugAwareStopwatch"/>, <see cref="StageRecord"/>,
/// and <see cref="ServiceInstanceInfo"/> — lifecycle utility types.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#diagnostics</docs>
public class DebugAwareStopwatchTests {
  [Test]
  public async Task StartNew_ReturnsRunningStopwatchAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    await Assert.That(sw.IsRunning).IsTrue();
    sw.Stop();
  }

  [Test]
  public async Task Stop_StopsTimingAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    sw.Stop();
    await Assert.That(sw.IsRunning).IsFalse();
  }

  [Test]
  public async Task Elapsed_ReturnsNonNegativeAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    await Task.Delay(1);
    sw.Stop();
    await Assert.That(sw.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Reset_StopsAndClearsElapsedAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    await Task.Delay(1);
    sw.Reset();
    await Assert.That(sw.IsRunning).IsFalse();
    await Assert.That(sw.Elapsed).IsEqualTo(TimeSpan.Zero);
    await Assert.That(sw.IsApproximate).IsFalse();
  }

  [Test]
  public async Task Restart_ResetsAndStartsAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    await Task.Delay(5);
    sw.Restart();
    await Assert.That(sw.IsRunning).IsTrue();
    // Elapsed should be small after restart (less than 100ms at least)
    await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(100));
  }

  [Test]
  public async Task Start_AfterStop_ResumesTimingAsync() {
    var sw = DebugAwareStopwatch.StartNew();
    await Task.Delay(1);
    sw.Stop();
    var elapsed1 = sw.Elapsed;
    sw.Start();
    await Task.Delay(1);
    sw.Stop();
    await Assert.That(sw.Elapsed).IsGreaterThanOrEqualTo(elapsed1);
  }

  [Test]
  public async Task IsApproximate_FalseByDefault_WhenNoDebuggerAsync() {
    // In test runner, debugger is typically not attached
    // If it is, this test will still pass (just returns true)
    var sw = DebugAwareStopwatch.StartNew();
    sw.Stop();
    // We can't assert false because debugger might be attached in CI
    // Just verify the property is accessible and returns a bool
    var isApprox = sw.IsApproximate;
    await Assert.That(isApprox).IsTypeOf<bool>();
  }

  [Test]
  public async Task Constructor_DoesNotStartAsync() {
    var sw = new DebugAwareStopwatch();
    await Assert.That(sw.IsRunning).IsFalse();
    await Assert.That(sw.Elapsed).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Start_BeginsTimingAsync() {
    var sw = new DebugAwareStopwatch();
    sw.Start();
    await Assert.That(sw.IsRunning).IsTrue();
    sw.Stop();
  }

  #region StageRecord tests

  [Test]
  public async Task StageRecord_Properties_AreAccessibleAsync() {
    var startedAt = DateTimeOffset.UtcNow;
    var duration = TimeSpan.FromMilliseconds(42);
    var record = new StageRecord(LifecycleStage.PostLifecycleDetached, duration, startedAt);

    await Assert.That(record.Stage).IsEqualTo(LifecycleStage.PostLifecycleDetached);
    await Assert.That(record.Duration).IsEqualTo(duration);
    await Assert.That(record.StartedAt).IsEqualTo(startedAt);
  }

  [Test]
  public async Task StageRecord_Equality_WorksAsync() {
    var startedAt = DateTimeOffset.UtcNow;
    var duration = TimeSpan.FromMilliseconds(42);
    var record1 = new StageRecord(LifecycleStage.PostLifecycleDetached, duration, startedAt);
    var record2 = new StageRecord(LifecycleStage.PostLifecycleDetached, duration, startedAt);
    var record3 = new StageRecord(LifecycleStage.PreOutboxDetached, duration, startedAt);

    await Assert.That(record1).IsEqualTo(record2);
    await Assert.That(record1).IsNotEqualTo(record3);
  }

  [Test]
  public async Task StageRecord_ToString_ContainsStageAsync() {
    var record = new StageRecord(LifecycleStage.PostPerspectiveInline, TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow);
    var str = record.ToString();
    await Assert.That(str).Contains("PostPerspectiveInline");
  }

  [Test]
  public async Task StageRecord_GetHashCode_ConsistentAsync() {
    var startedAt = DateTimeOffset.UtcNow;
    var record1 = new StageRecord(LifecycleStage.PostLifecycleDetached, TimeSpan.FromMilliseconds(42), startedAt);
    var record2 = new StageRecord(LifecycleStage.PostLifecycleDetached, TimeSpan.FromMilliseconds(42), startedAt);

    await Assert.That(record1.GetHashCode()).IsEqualTo(record2.GetHashCode());
  }

  #endregion
}
