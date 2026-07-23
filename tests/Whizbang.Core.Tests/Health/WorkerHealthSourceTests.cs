using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers <see cref="WorkerHealthSource"/>: closed gate ⇒ <see cref="ComponentState.PausedByDesign"/>
/// (workers intentionally held, healthy under the Lenient default), open gate ⇒ Operational.
/// </summary>
public class WorkerHealthSourceTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; } = ready;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
  }

  [Test]
  public async Task GateClosed_ReportsPausedByDesignAsync() {
    var source = new WorkerHealthSource(new FakeGate(ready: false));
    var health = await source.ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.PausedByDesign);
  }

  [Test]
  public async Task GateOpen_ReportsOperationalAsync() {
    var source = new WorkerHealthSource(new FakeGate(ready: true));
    var health = await source.ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task Component_IsWorkersAsync() {
    var source = new WorkerHealthSource(new FakeGate(ready: false));
    await Assert.That(source.Component).IsEqualTo("workers");
  }
}
