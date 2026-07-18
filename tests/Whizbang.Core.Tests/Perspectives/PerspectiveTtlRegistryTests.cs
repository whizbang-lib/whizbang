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
}
