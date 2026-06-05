using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4 of zero-idle-polling — locks the
/// <see cref="BackupTickRegistry"/> contract.
/// </summary>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
public class BackupTickRegistryTests {

  [Test]
  public async Task NewRegistry_HasNoRegistrationsAsync() {
    var registry = new BackupTickRegistry();

    await Assert.That(registry.Registrations).IsEmpty();
  }

  [Test]
  public async Task Register_AddsToRegistrationsAsync() {
    var registry = new BackupTickRegistry();

    registry.Register("test", _ => Task.CompletedTask, () => true);

    await Assert.That(registry.Registrations).Count().IsEqualTo(1);
    await Assert.That(registry.Registrations[0].Name).IsEqualTo("test");
  }

  [Test]
  public async Task Register_PreservesRegistrationOrderAsync() {
    var registry = new BackupTickRegistry();

    registry.Register("first", _ => Task.CompletedTask, () => true);
    registry.Register("second", _ => Task.CompletedTask, () => true);
    registry.Register("third", _ => Task.CompletedTask, () => true);

    await Assert.That(registry.Registrations.Select(r => r.Name).ToArray())
      .IsEquivalentTo(["first", "second", "third"])
      .Because("Coordinator iterates in registration order so operators get deterministic per-tick log ordering.");
  }

  [Test]
  public async Task Registrations_ReturnsSnapshotAsync() {
    var registry = new BackupTickRegistry();
    registry.Register("a", _ => Task.CompletedTask, () => true);

    var snapshot = registry.Registrations;
    registry.Register("b", _ => Task.CompletedTask, () => true);

    await Assert.That(snapshot).Count().IsEqualTo(1)
      .Because("Returning a snapshot rather than the live list lets the coordinator iterate without lock contention against concurrent Register calls.");
  }

  [Test]
  public async Task Register_NullName_ThrowsAsync() {
    var registry = new BackupTickRegistry();

    await Assert.That(() => registry.Register(null!, _ => Task.CompletedTask, () => true))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Register_NullTick_ThrowsAsync() {
    var registry = new BackupTickRegistry();

    await Assert.That(() => registry.Register("test", null!, () => true))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Register_NullIsEnabled_ThrowsAsync() {
    var registry = new BackupTickRegistry();

    await Assert.That(() => registry.Register("test", _ => Task.CompletedTask, null!))
      .Throws<ArgumentNullException>();
  }
}
