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
  public async Task HasAnyConsumer_RuntimeRegisteredType_CountsAsConsumerAsync() {
    // The integrity/control-plane receptors are RUNTIME-registered (driver hosted services),
    // invisible to the source-generated query. If the adapter consults only the generated
    // tables, the receive/inbox discard gates silently drop every control-plane message —
    // observed live: checkpoints/rebuild commands discarded as "no consumer" on services whose
    // runtime registrars had them.
    var runtime = new _runtimeRegistryFake(UNKNOWN_TYPE);
    var sut = new WhizbangReceptorRegistryQueryAdapter(runtime);

    await Assert.That(sut.HasAnyConsumer(UNKNOWN_TYPE)).IsTrue()
      .Because("a runtime-registered receptor IS a consumer — both discard gates route " +
               "through this answer.");
    await Assert.That(sut.HasAnyConsumer(UNKNOWN_TYPE + ".Other")).IsFalse()
      .Because("unknown everywhere stays discardable.");
  }

  [Test]
  public async Task GeneratedRegistry_RuntimeRegister_AnswersHasRuntimeConsumerForAsync() {
    // End-to-end through the REAL generated registry: a runtime Register<T> must make the type
    // visible to the discard gates via HasRuntimeConsumerFor, in the storage name form.
    var registry = new Whizbang.Core.Tests.Generated.GeneratedReceptorRegistry();
    var storedName = TypeNameFormatter.Format(typeof(RuntimeConsumerProbeMessage));
    await Assert.That(registry.HasRuntimeConsumerFor(storedName)).IsFalse()
      .Because("nothing runtime-registered yet.");

    registry.Register<RuntimeConsumerProbeMessage>(new RuntimeConsumerProbeReceptor(), LifecycleStage.PostInboxInline);

    await Assert.That(registry.HasRuntimeConsumerFor(storedName)).IsTrue();
    await Assert.That(registry.HasRuntimeConsumerFor(typeof(RuntimeConsumerProbeMessage).FullName!)).IsTrue()
      .Because("the bare FullName form must match too.");
    await Assert.That(new WhizbangReceptorRegistryQueryAdapter(registry).HasAnyConsumer(storedName)).IsTrue()
      .Because("the adapter surfaces the runtime registration to both discard gates.");
  }

  private sealed class _runtimeRegistryFake(string knownName) : IReceptorRegistry {
    public bool HasRuntimeConsumerFor(string clrTypeName) => clrTypeName == knownName;
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
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

/// <summary>Probe message for the runtime-consumer registry test (public: the generator emits dispatcher glue for it).</summary>
public sealed record RuntimeConsumerProbeMessage : IMessage;

/// <summary>Probe receptor for the runtime-consumer registry test.</summary>
public sealed class RuntimeConsumerProbeReceptor : IReceptor<RuntimeConsumerProbeMessage> {
  /// <inheritdoc />
  public ValueTask HandleAsync(RuntimeConsumerProbeMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
