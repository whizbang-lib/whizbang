using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Surface tests for the <see cref="PendingRename"/> record — used by the
/// rename tool to carry one detected drift (old → new CLR type name) for a
/// pinned id. PinnedId is nullable because manual <c>Rename()</c> calls
/// don't know it; row matching then falls back to OldClrTypeName.
///
/// Coverage report shows 0% — the production rename tool only runs as a
/// CLI on demand, so the record had no direct unit tests. Pin the ctor
/// arity + nullable contract + value equality.
/// </summary>
/// <docs>core-concepts/pinned-identity</docs>
public class PendingRenameTests {

  [Test]
  public async Task PositionalCtor_RoundTripsAllValuesAsync() {
    var r = new PendingRename(
      PinnedId: "8a3f1c2e-0001-7000-8000-000000000001",
      OldClrTypeName: "MyApp.Events.OrderCreated",
      NewClrTypeName: "MyApp.Orders.Events.OrderCreated");

    await Assert.That(r.PinnedId).IsEqualTo("8a3f1c2e-0001-7000-8000-000000000001");
    await Assert.That(r.OldClrTypeName).IsEqualTo("MyApp.Events.OrderCreated");
    await Assert.That(r.NewClrTypeName).IsEqualTo("MyApp.Orders.Events.OrderCreated");
  }

  [Test]
  public async Task PinnedId_AllowsNullAsync() {
    // Manual Rename() path — caller doesn't supply a pinned id; the tool
    // matches the registry row by OldClrTypeName instead.
    var r = new PendingRename(PinnedId: null, OldClrTypeName: "Old", NewClrTypeName: "New");

    await Assert.That(r.PinnedId).IsNull();
    await Assert.That(r.OldClrTypeName).IsEqualTo("Old");
  }

  [Test]
  public async Task RecordValueEqualityAsync() {
    var a = new PendingRename("p1", "OldT", "NewT");
    var b = new PendingRename("p1", "OldT", "NewT");

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task DifferentPinnedId_NotEqualAsync() {
    var a = new PendingRename("p1", "Old", "New");
    var b = new PendingRename("p2", "Old", "New");

    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task NullPinnedId_NotEqualToNonNullAsync() {
    var a = new PendingRename(null, "Old", "New");
    var b = new PendingRename("p1", "Old", "New");

    await Assert.That(a).IsNotEqualTo(b);
  }
}
