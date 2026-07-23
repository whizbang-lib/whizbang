using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers <see cref="SchemaHealthSource"/>: closed gate ⇒ <see cref="ComponentState.Migrating"/>
/// (ready under the Lenient default), open gate ⇒ <see cref="ComponentState.Operational"/>. This is
/// the source that stops a long startup migration from reading as a failure.
/// </summary>
public class SchemaHealthSourceTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; private set; } = ready;
    public void MarkReady() => IsReady = true;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
  }

  [Test]
  public async Task GateClosed_ReportsMigratingAsync() {
    var source = new SchemaHealthSource(new FakeGate(ready: false));
    var health = await source.ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Migrating);
  }

  [Test]
  public async Task GateOpen_ReportsOperationalAsync() {
    var source = new SchemaHealthSource(new FakeGate(ready: true));
    var health = await source.ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task Component_IsSchemaAsync() {
    var source = new SchemaHealthSource(new FakeGate(ready: false));
    await Assert.That(source.Component).IsEqualTo("schema");
  }
}
