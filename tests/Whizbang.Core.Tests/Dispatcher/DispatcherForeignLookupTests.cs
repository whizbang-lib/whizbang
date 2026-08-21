using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Issue #491, the routing half: receptor discovery is source-only, so each assembly's generated
/// dispatcher can route ONLY its own receptors — and whichever assembly's dispatcher wins DI
/// resolution leaves every other assembly's receptors unreachable, silently. The fix: every
/// assembly's generated dispatcher is also registered as an <see cref="IReceptorLookup"/>, and the
/// base dispatcher falls back to those foreign lookups when its own table answers null — sends
/// take the first foreign match, publishes fan out to EVERY foreign assembly's receptors.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/IReceptorLookup.cs</code-under-test>
[Category("Startup")]
public class DispatcherForeignLookupTests {

  private sealed record ForeignCommand(Guid Id);
  private sealed record ForeignEvent(Guid Id);

  /// <summary>A host dispatcher whose OWN tables know nothing — every lookup returns null,
  /// exactly what a generated dispatcher answers for another assembly's types.</summary>
  private sealed class _blindDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType)
      => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType)
      => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType)
      => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType)
      => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType)
      => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType)
      => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType)
      => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType)
      => null;
  }

  /// <summary>What a foreign assembly's generated dispatcher contributes: its own tables.</summary>
  private sealed class _foreignAssemblyLookup(string name) : IReceptorLookup {
    private int _invoked;
    private int _published;
    public string Name => name;
    public int Invoked => Volatile.Read(ref _invoked);
    public int Published => Volatile.Read(ref _published);

    public ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType != typeof(ForeignCommand)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _invoked);
        return ValueTask.FromResult<TResult>(default!);
      };
    }
    public VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType) => null;
    public Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType) {
      if (eventType != typeof(ForeignEvent)) {
        return null;
      }
      return (_, _, _) => {
        Interlocked.Increment(ref _published);
        return Task.CompletedTask;
      };
    }
    public SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType) => null;
    public Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType) {
      if (messageType != typeof(ForeignCommand)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _invoked);
        return ValueTask.FromResult<object?>(null);
      };
    }
    public DispatchModes? LookupReceptorDefaultRouting(Type messageType)
      => messageType == typeof(ForeignCommand) ? DispatchModes.LocalDispatch : null;
  }

  private static ServiceProvider _buildHost(params IReceptorLookup[] lookups) {
    var services = new ServiceCollection();
    foreach (var lookup in lookups) {
      services.AddSingleton(lookup);
    }
    return services.BuildServiceProvider();
  }

  [Test]
  [Timeout(30000)]
  public async Task Send_WhenOnlyAForeignAssemblyKnowsTheReceptor_InvokesItAsync(CancellationToken cancellationToken) {
    var foreign = new _foreignAssemblyLookup("contracts-assembly");
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    _ = await dispatcher.SendAsync(new ForeignCommand(Guid.NewGuid()));

    await Assert.That(foreign.Invoked).IsEqualTo(1)
      .Because("issue #491: a receptor declared in another assembly must still run — its assembly's "
             + "lookup is registered, and the winning dispatcher must consult it");
  }

  [Test]
  [Timeout(30000)]
  public async Task Publish_FansOutToEveryForeignAssemblysReceptorsAsync(CancellationToken cancellationToken) {
    var contracts = new _foreignAssemblyLookup("contracts-assembly");
    var billing = new _foreignAssemblyLookup("billing-assembly");
    await using var sp = _buildHost(contracts, billing);
    var dispatcher = new _blindDispatcher(sp);

    await dispatcher.PublishAsync(new ForeignEvent(Guid.NewGuid()));

    await Assert.That(contracts.Published).IsEqualTo(1)
      .Because("publish is a broadcast: every assembly with receptors for the event gets it");
    await Assert.That(billing.Published).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task Send_WithNoLookupAnywhere_KeepsTodaysBehaviourAsync(CancellationToken cancellationToken) {
    await using var sp = _buildHost();
    var dispatcher = new _blindDispatcher(sp);

    await Assert.ThrowsAsync<ReceptorNotFoundException>(async () =>
      await dispatcher.SendAsync(new ForeignCommand(Guid.NewGuid())));
    // hosts without foreign lookups behave exactly as before — the fallback is additive
  }
}
