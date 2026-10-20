// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.MediatorLookup.Owned;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1418, against the REAL generated dispatcher: a receptor returns a value that is neither a command nor an
/// event and has no receptor of its exact type. The value now goes to the receptors of an interface or base
/// type it implements, as a mediator would, and with none the call fails, unless
/// <see cref="WhizbangCoreOptions.RequireReceptorForReturnedResult"/> is off. Before the fix it was skipped
/// with a warning.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/message-cascade#mediator-lookup</docs>
public class MediatorResultLookupComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.MediatorLookup.Owned";

  [Test]
  public async Task AReturnedValueWithNoReceptorOfItsOwn_ReachesAnotherAssemblysReceptorOfItsInterfaceAsync() {
    var foreign = new InterfaceOnlyLookup();
    await using var provider = _buildProvider(foreignLookup: foreign);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await dispatcher.LocalInvokeAsync(new MdAskForForeignAnswer(id));

    await Assert.That(foreign.Published.Select(m => ((IMdForeignAnswer)m).Id).ToArray()).IsEquivalentTo([id])
      .Because("the answer has no receptor of its own here, so it goes to the receptor another assembly declares for its interface");
  }

  [Test]
  public async Task AReturnedValueWithNoReceptorOfItsOwn_ReachesARuntimeReceptorOfItsInterfaceAsync() {
    await using var provider = _buildProvider();
    provider.GetRequiredService<IReceptorRegistry>()
      .Register(new MdRuntimeInterfaceReceptor<IMdRuntimeAnswer>(), LifecycleStage.LocalImmediateInline);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await dispatcher.LocalInvokeAsync(new MdAskForRuntimeAnswer(id));

    await Assert.That(MediatorProbe.Ran(id)).IsEquivalentTo(["runtime-interface-receptor"])
      .Because("a receptor registered at runtime for an interface of the answer handles it");
  }

  [Test]
  public async Task AReturnedValueNothingHandles_FailsTheCallByDefaultAsync() {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await Assert.That(async () => await dispatcher.LocalInvokeAsync(new MdAskForUnhandledAnswer(id)))
      .Throws<ReceptorNotFoundException>()
      .Because("a returned value no receptor handles, by any type, is refused by default");
    await Assert.That(MediatorProbe.Ran(id)).IsEquivalentTo(["asked"]);
  }

  [Test]
  public async Task AReturnedValueNothingHandles_IsSkippedWhenNotRequiredAsync() {
    await using var provider = _buildProvider(requireReceptor: false);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await dispatcher.LocalInvokeAsync(new MdAskForUnhandledAnswer(id));

    await Assert.That(MediatorProbe.Ran(id)).IsEquivalentTo(["asked"])
      .Because("with RequireReceptorForReturnedResult off the value is skipped and the call succeeds");
  }

  /// <summary>Another assembly's lookup: one receptor, declared for <see cref="IMdForeignAnswer"/>.</summary>
  private sealed class InterfaceOnlyLookup : IReceptorLookup {
    public List<object> Published { get; } = [];

    public IReadOnlyList<Type> LookupPolymorphicReceptorMessageTypes() => [typeof(IMdForeignAnswer)];

    public Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType) =>
      eventType == typeof(IMdForeignAnswer) ? (message, _, _) => _recordAsync(message) : null;

    private Task _recordAsync(object message) {
      Published.Add(message);
      return Task.CompletedTask;
    }

    public ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType) => null;
    public SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType) => null;
    public Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType) => null;
    public DispatchModes? LookupReceptorDefaultRouting(Type messageType) => null;
  }

  private static ServiceProvider _buildProvider(bool requireReceptor = true, IReceptorLookup? foreignLookup = null) {
    var services = new ServiceCollection();
    if (foreignLookup is not null) {
      services.AddSingleton(foreignLookup);
    }
    services.AddSingleton(new WhizbangCoreOptions { RequireReceptorForReturnedResult = requireReceptor });
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
