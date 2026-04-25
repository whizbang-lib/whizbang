using Whizbang.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for <see cref="UpdateStreamScopeCommand"/> shape and the
/// <see cref="PerspectiveScope.MergeWith"/> helper that backs <see cref="ScopeMutationMode.Merge"/>.
/// </summary>
/// <tests>UpdateStreamScopeCommand,ScopeMutationMode,PerspectiveScope.MergeWith</tests>
public class UpdateStreamScopeCommandTests {
  // ===== Command shape =====

  [Test]
  public async Task Command_RequiredProperties_AreSetAsync() {
    var streamId = Guid.NewGuid();
    var scope = new PerspectiveScope { TenantId = "t-1" };
    var cmd = new UpdateStreamScopeCommand {
      StreamId = streamId,
      NewScope = scope,
    };
    var sid = cmd.StreamId;
    var ns = cmd.NewScope;
    var mode = cmd.Mode;
    await Assert.That(sid).IsEqualTo(streamId);
    await Assert.That(ns).IsSameReferenceAs(scope);
    await Assert.That(mode).IsEqualTo(ScopeMutationMode.Replace);
  }

  [Test]
  public async Task Command_Mode_AcceptsMergeAsync() {
    var cmd = new UpdateStreamScopeCommand {
      StreamId = Guid.NewGuid(),
      NewScope = new PerspectiveScope(),
      Mode = ScopeMutationMode.Merge,
    };
    var mode = cmd.Mode;
    await Assert.That(mode).IsEqualTo(ScopeMutationMode.Merge);
  }

  [Test]
  public async Task Command_IsICommandAsync() {
    var cmd = new UpdateStreamScopeCommand {
      StreamId = Guid.NewGuid(),
      NewScope = new PerspectiveScope(),
    };
    var asCmd = cmd as ICommand;
    await Assert.That(asCmd).IsNotNull();
  }

  // ===== MergeWith semantics =====

  [Test]
  public async Task MergeWith_NonEmptyOther_OverwritesScalarFieldsAsync() {
    var existing = new PerspectiveScope { TenantId = "t-1", UserId = "u-1", CustomerId = "c-1" };
    var update = new PerspectiveScope { UserId = "u-2" };

    var merged = existing.MergeWith(update);

    var t = merged.TenantId;
    var u = merged.UserId;
    var c = merged.CustomerId;
    await Assert.That(t).IsEqualTo("t-1");
    await Assert.That(u).IsEqualTo("u-2");
    await Assert.That(c).IsEqualTo("c-1");
  }

  [Test]
  public async Task MergeWith_NullFieldsOnOther_PreserveExistingAsync() {
    var existing = new PerspectiveScope { TenantId = "t-1", UserId = "u-1" };
    var update = new PerspectiveScope();

    var merged = existing.MergeWith(update);

    var t = merged.TenantId;
    var u = merged.UserId;
    await Assert.That(t).IsEqualTo("t-1");
    await Assert.That(u).IsEqualTo("u-1");
  }

  [Test]
  public async Task MergeWith_EmptyStringsOnOther_PreserveExistingAsync() {
    var existing = new PerspectiveScope { TenantId = "t-1" };
    var update = new PerspectiveScope { TenantId = "" };

    var merged = existing.MergeWith(update);

    var t = merged.TenantId;
    await Assert.That(t).IsEqualTo("t-1");
  }

  [Test]
  public async Task MergeWith_AllowedPrincipals_ConcatenatesAndDedupesAsync() {
    var existing = new PerspectiveScope { AllowedPrincipals = ["user:a", "group:x"] };
    var update = new PerspectiveScope { AllowedPrincipals = ["user:b", "group:x"] };

    var merged = existing.MergeWith(update);

    var aps = merged.AllowedPrincipals;
    await Assert.That(aps.Count).IsEqualTo(3);
    await Assert.That(aps.Contains("user:a")).IsTrue();
    await Assert.That(aps.Contains("user:b")).IsTrue();
    await Assert.That(aps.Contains("group:x")).IsTrue();
  }

  [Test]
  public async Task MergeWith_Extensions_NewKeysAddedExistingValuesUpdatedAsync() {
    var existing = new PerspectiveScope { Extensions = [new("region", "us-west"), new("tier", "gold")] };
    var update = new PerspectiveScope { Extensions = [new("region", "us-east"), new("zone", "a")] };

    var merged = existing.MergeWith(update);

    var byKey = merged.Extensions.ToDictionary(e => e.Key, e => e.Value);
    await Assert.That(byKey.Count).IsEqualTo(3);
    await Assert.That(byKey["region"]).IsEqualTo("us-east");
    await Assert.That(byKey["tier"]).IsEqualTo("gold");
    await Assert.That(byKey["zone"]).IsEqualTo("a");
  }

  [Test]
  public async Task MergeWith_DoesNotMutateInputsAsync() {
    var existing = new PerspectiveScope { TenantId = "t-1", AllowedPrincipals = ["user:a"] };
    var update = new PerspectiveScope { TenantId = "t-2", AllowedPrincipals = ["user:b"] };

    var merged = existing.MergeWith(update);

    var existingTenant = existing.TenantId;
    var existingApsCount = existing.AllowedPrincipals.Count;
    var updateApsCount = update.AllowedPrincipals.Count;
    var sameRefAsExisting = ReferenceEquals(merged, existing);
    await Assert.That(existingTenant).IsEqualTo("t-1");
    await Assert.That(existingApsCount).IsEqualTo(1);
    await Assert.That(updateApsCount).IsEqualTo(1);
    await Assert.That(sameRefAsExisting).IsFalse();
  }
}
