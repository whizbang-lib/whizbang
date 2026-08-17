using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="PerspectiveTtlRegistry"/> — the model-type -&gt; row-TTL map the perspective-runner
/// generator populates for TtlRow perspectives and the EF Core upsert consults to stamp <c>expires_at</c>.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/Perspectives/PerspectiveTtlRegistry.cs</tests>
public class PerspectiveTtlRegistryTests {
  private sealed class RegisteredModel;
  private sealed class UnregisteredModel;

  [Test]
  public async Task Register_ThenResolve_ReturnsTheSecondsAsync() {
    PerspectiveTtlRegistry.Register(typeof(RegisteredModel), 7200);
    await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(RegisteredModel))).IsEqualTo(7200)
      .Because("A registered TtlRow model resolves to its row TTL in seconds.");
  }

  [Test]
  public async Task ResolveSeconds_Unregistered_ReturnsMinusOneAsync() {
    await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(UnregisteredModel))).IsEqualTo(-1)
      .Because("An unregistered model (PersistedRow/InMemory/Sourced) never expires.");
  }

  private sealed class OverriddenModel;
  private sealed class DisabledModel;

  [Test]
  [NotInParallel("PerspectiveTtlRegistryRuntimeConfig")]
  public async Task RuntimeOverride_WinsOverRegisteredValueAsync() {
    // The operator rung of the override ladder (perspective row retention): a runtime
    // configuration override — keyed by the model's full CLR name — outranks the
    // compile-time registration, so a TTL can be retuned per environment without a redeploy.
    PerspectiveTtlRegistry.Register(typeof(OverriddenModel), 3600);
    try {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(
        enabled: true,
        overrides: new Dictionary<string, int?> { [typeof(OverriddenModel).FullName!] = 7776000 });
      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(OverriddenModel))).IsEqualTo(7776000)
        .Because("a runtime override outranks the generated registration");

      // A null override disables retention for just that model.
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(
        enabled: true,
        overrides: new Dictionary<string, int?> { [typeof(OverriddenModel).FullName!] = null });
      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(OverriddenModel))).IsEqualTo(-1)
        .Because("a null override switches that model's rows back to never-expiring");
    } finally {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(enabled: true, overrides: null);
    }
  }

  [Test]
  [NotInParallel("PerspectiveTtlRegistryRuntimeConfig")]
  public async Task Disabled_KillSwitch_ResolvesEverythingToMinusOneAsync() {
    // The global kill switch: with retention disabled, every model resolves to -1 — stamping
    // stops, the lens expiry filter stops hiding, and the resurrection probe stops firing —
    // one consult point keeps all three seams coherent. (Already-stamped rows may still reap
    // physically until their stamps drain; sourced rows are recoverable via resurrection.)
    PerspectiveTtlRegistry.Register(typeof(DisabledModel), 3600);
    try {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(enabled: false, overrides: null);
      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(DisabledModel))).IsEqualTo(-1)
        .Because("the kill switch turns row retention off everywhere at once");
    } finally {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(enabled: true, overrides: null);
      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(DisabledModel))).IsEqualTo(3600)
        .Because("re-enabling restores the registered values untouched");
    }
  }
}
