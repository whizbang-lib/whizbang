using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Adapter tests for <see cref="WhizbangReceptorRegistryQueryAdapter"/> — the
/// instance-API shim that delegates each call to the source-generated
/// <c>Whizbang.Core.Generated.WhizbangReceptorRegistryQuery</c> static class.
///
/// The adapter exists ONLY so DI consumers can resolve <see cref="IReceptorRegistryQuery"/>
/// (test code can substitute a fake; production binds to this implementation).
/// The tests below pin the delegation: each instance method returns the same value
/// as the corresponding static, for every shape of input.
///
/// Cache pollution risk: the static caches contributions across the AppDomain.
/// We query for type names the test owns (a freshly-declared `_NoSuchType`) which
/// can never be registered — the result is always false, regardless of what other
/// tests registered. No mutation of the static state, no flake.
/// </summary>
/// <docs>internals/receptor-registry-query</docs>
public class WhizbangReceptorRegistryQueryAdapterTests {

  // A type name no source generator will ever produce — unique per this file.
  private const string UNKNOWN_TYPE = "Whizbang.Core.Tests.NeverRegistered.Type_e0b6c2b8";

  [Test]
  public async Task HasReceptors_UnknownType_DelegatesAndReturnsFalseAsync() {
    var sut = new WhizbangReceptorRegistryQueryAdapter();

    var direct = sut.HasReceptors(LifecycleStage.PreDistributeDetached, UNKNOWN_TYPE);
    var static_ = Whizbang.Core.Generated.WhizbangReceptorRegistryQuery
      .HasReceptors(LifecycleStage.PreDistributeDetached, UNKNOWN_TYPE);

    await Assert.That(direct).IsEqualTo(static_);
    await Assert.That(direct).IsFalse();
  }

  [Test]
  public async Task HasInboxHandler_UnknownType_DelegatesAndReturnsFalseAsync() {
    var sut = new WhizbangReceptorRegistryQueryAdapter();

    var direct = sut.HasInboxHandler(UNKNOWN_TYPE);
    var static_ = Whizbang.Core.Generated.WhizbangReceptorRegistryQuery
      .HasInboxHandler(UNKNOWN_TYPE);

    await Assert.That(direct).IsEqualTo(static_);
    await Assert.That(direct).IsFalse();
  }

  [Test]
  public async Task HasAnyConsumer_UnknownType_DelegatesAndReturnsFalseAsync() {
    var sut = new WhizbangReceptorRegistryQueryAdapter();

    var direct = sut.HasAnyConsumer(UNKNOWN_TYPE);
    var static_ = Whizbang.Core.Generated.WhizbangReceptorRegistryQuery
      .HasAnyConsumer(UNKNOWN_TYPE);

    await Assert.That(direct).IsEqualTo(static_);
    await Assert.That(direct).IsFalse();
  }

  [Test]
  public async Task Adapter_ImplementsIReceptorRegistryQueryAsync() {
    // Compile-time fact, but explicitly asserted here so a future rename / shape
    // change of IReceptorRegistryQuery breaks this test before it breaks DI.
    var sut = new WhizbangReceptorRegistryQueryAdapter();
    await Assert.That(sut).IsAssignableTo<IReceptorRegistryQuery>();
  }
}
