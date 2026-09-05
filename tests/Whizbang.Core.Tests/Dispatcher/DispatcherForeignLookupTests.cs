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

  // ============================================================
  // The remaining receptor shapes
  // ============================================================
  //
  // A receptor's shape — async or sync, returning a value or void — is the author's choice, made
  // in whichever assembly declares it. The cross-assembly fallback therefore has to cover every
  // shape: a lookup that only consulted foreign assemblies for async value-returning receptors
  // would leave a sync void receptor in another assembly just as unreachable as before issue #491,
  // and just as silently.

  private sealed record VoidCommand(Guid Id);
  private sealed record SyncQuery(Guid Id);
  private sealed record VoidSyncCommand(Guid Id);

  /// <summary>A foreign assembly whose receptors span every shape the dispatcher can resolve.</summary>
  private sealed class _allShapesLookup : IReceptorLookup {
    private int _asyncVoid;
    private int _sync;
    private int _syncVoid;
    private int _any;

    public int AsyncVoidInvoked => Volatile.Read(ref _asyncVoid);
    public int SyncInvoked => Volatile.Read(ref _sync);
    public int SyncVoidInvoked => Volatile.Read(ref _syncVoid);
    public int AnyInvoked => Volatile.Read(ref _any);
    public DispatchModes? RoutingFor { get; set; }

    public ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType) => null;

    public VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType) {
      if (messageType != typeof(VoidCommand)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _asyncVoid);
        return ValueTask.CompletedTask;
      };
    }

    public Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType) => null;

    public SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType != typeof(SyncQuery)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _sync);
        return default!;
      };
    }

    public VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType) {
      if (messageType != typeof(VoidSyncCommand)) {
        return null;
      }
      return _ => Interlocked.Increment(ref _syncVoid);
    }

    public Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType) {
      if (messageType != typeof(SyncQuery)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _any);
        return ValueTask.FromResult<object?>(null);
      };
    }

    public DispatchModes? LookupReceptorDefaultRouting(Type messageType) => RoutingFor;
  }

  [Test]
  [Timeout(30000)]
  public async Task LocalInvoke_ResolvesAForeignAsyncVoidReceptorAsync(CancellationToken cancellationToken) {
    var foreign = new _allShapesLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    await dispatcher.LocalInvokeAsync(new VoidCommand(Guid.NewGuid()));

    await Assert.That(foreign.AsyncVoidInvoked).IsEqualTo(1)
      .Because("a void receptor in another assembly is as unreachable as a value-returning one "
             + "unless the void lookup consults foreign assemblies too");
  }

  [Test]
  [Timeout(30000)]
  public async Task LocalInvoke_ResolvesAForeignSyncReceptorAsync(CancellationToken cancellationToken) {
    // Sync is the fallback the dispatcher tries only after the async table answers null, so the
    // foreign consultation has to happen on that second pass as well.
    var foreign = new _allShapesLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    _ = await dispatcher.LocalInvokeAsync<string>(new SyncQuery(Guid.NewGuid()));

    await Assert.That(foreign.SyncInvoked).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task LocalInvoke_ResolvesAForeignSyncVoidReceptorAsync(CancellationToken cancellationToken) {
    var foreign = new _allShapesLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    await dispatcher.LocalInvokeAsync(new VoidSyncCommand(Guid.NewGuid()));

    await Assert.That(foreign.SyncVoidInvoked).IsEqualTo(1)
      .Because("the narrowest shape — sync, void — is the last fallback and the easiest to leave out");
  }

  [Test]
  [Timeout(30000)]
  public async Task ForeignLookup_IsConsultedOnlyWhenTheOwnTableAnswersNullAsync(CancellationToken cancellationToken) {
    // The fallback is additive: an assembly that can answer for itself must never hand the
    // message to a foreign assembly, or a publish would double-deliver.
    var foreign = new _allShapesLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _knowsVoidCommandDispatcher(sp);

    await dispatcher.LocalInvokeAsync(new VoidCommand(Guid.NewGuid()));

    await Assert.That(dispatcher.OwnInvoked).IsEqualTo(1);
    await Assert.That(foreign.AsyncVoidInvoked).IsEqualTo(0)
      .Because("consulting foreign lookups after a local hit would run the receptor twice");
  }

  [Test]
  [Timeout(30000)]
  public async Task ForeignLookup_TakesTheFirstAssemblyThatAnswersForASendAsync(CancellationToken cancellationToken) {
    // Sends have exactly one handler by definition, so the scan stops at the first match rather
    // than fanning out the way a publish does.
    var first = new _allShapesLookup();
    var second = new _allShapesLookup();
    await using var sp = _buildHost(first, second);
    var dispatcher = new _blindDispatcher(sp);

    await dispatcher.LocalInvokeAsync(new VoidCommand(Guid.NewGuid()));

    await Assert.That(first.AsyncVoidInvoked + second.AsyncVoidInvoked).IsEqualTo(1)
      .Because("a send is not a broadcast — running it in two assemblies would duplicate its effects");
  }

  [Test]
  [Timeout(30000)]
  public async Task ForeignLookup_SkipsAnAssemblyThatDoesNotKnowTheTypeAsync(CancellationToken cancellationToken) {
    // The scan has to keep walking past assemblies that answer null, not stop at the first one.
    var stranger = new _foreignAssemblyLookup("unrelated-assembly");
    var owner = new _allShapesLookup();
    await using var sp = _buildHost(stranger, owner);
    var dispatcher = new _blindDispatcher(sp);

    await dispatcher.LocalInvokeAsync(new VoidCommand(Guid.NewGuid()));

    await Assert.That(owner.AsyncVoidInvoked).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task UnknownMessage_StillThrowsAfterEveryForeignLookupAnswersNullAsync(CancellationToken cancellationToken) {
    // The scan must terminate in the same ReceptorNotFoundException as before — a message nobody
    // handles has to stay a loud error, not become a silent no-op once lookups are registered.
    var foreign = new _allShapesLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    await Assert.ThrowsAsync<ReceptorNotFoundException>(async () =>
      await dispatcher.LocalInvokeAsync(new ForeignCommand(Guid.NewGuid())));
  }

  /// <summary>A host dispatcher whose own table answers for <see cref="VoidCommand"/>.</summary>
  private sealed class _knowsVoidCommandDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: null)) {
    private int _own;
    public int OwnInvoked => Volatile.Read(ref _own);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType)
      => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType != typeof(VoidCommand)) {
        return null;
      }
      return _ => {
        Interlocked.Increment(ref _own);
        return ValueTask.CompletedTask;
      };
    }
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

  private sealed record RpcCommand(Guid Id);
  private sealed record RpcResponse(string Code);
  private sealed record CascadedNote([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// A foreign assembly whose receptor returns a composite: the RPC response plus an event to
  /// cascade. Only the "any" invoker answers, which is the shape the dispatcher falls back to
  /// after both the async and sync tables miss.
  /// </summary>
  private sealed class _rpcExtractionLookup : IReceptorLookup {
    private int _anyLookups;
    private int _routingLookups;

    public int AnyLookups => Volatile.Read(ref _anyLookups);
    public int RoutingLookups => Volatile.Read(ref _routingLookups);

    public ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType) => null;
    public Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType) => null;
    public SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType) => null;

    public Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType) {
      if (messageType != typeof(RpcCommand)) {
        return null;
      }
      Interlocked.Increment(ref _anyLookups);
      return msg => {
        var command = (RpcCommand)msg;
        return ValueTask.FromResult<object?>(
          (new RpcResponse("confirmed"), new CascadedNote(command.Id)));
      };
    }

    public DispatchModes? LookupReceptorDefaultRouting(Type messageType) {
      Interlocked.Increment(ref _routingLookups);
      return DispatchModes.Local;
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task LocalInvoke_ResolvesAForeignReceptorThatReturnsACompositeAsync(
      CancellationToken cancellationToken) {
    // The RPC-extraction fallback is the dispatcher's last resort: a receptor whose result
    // carries the response alongside events to cascade. Five of the seven lookup shapes already
    // consult foreign assemblies; this one did not, so a receptor in another assembly using the
    // composite return shape was unreachable -- the caller got ReceptorNotFoundException for a
    // receptor that plainly exists, which is issue #491 in the one form still uncovered.
    var foreign = new _rpcExtractionLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    var response = await dispatcher.LocalInvokeAsync<RpcResponse>(new RpcCommand(Guid.NewGuid()));

    await Assert.That(response.Code).IsEqualTo("confirmed")
      .Because("the response is extracted out of the composite and handed back to the caller");
    await Assert.That(foreign.AnyLookups).IsGreaterThanOrEqualTo(1)
      .Because("the own table answered null for every shape, so the foreign any-invoker is the "
             + "only thing that can serve this receptor");
  }

  [Test]
  [Timeout(30000)]
  public async Task Cascade_AsksTheForeignAssemblyForItsReceptorsDefaultRoutingAsync(
      CancellationToken cancellationToken) {
    // Messages cascaded out of a receptor's result inherit that receptor's declared routing.
    // For a foreign receptor the declaration lives in the other assembly's table, so skipping
    // the foreign consultation silently routes its cascade by the ambient default instead --
    // a receptor that declared Outbox would have its events handled locally and lose durability.
    var foreign = new _rpcExtractionLookup();
    await using var sp = _buildHost(foreign);
    var dispatcher = new _blindDispatcher(sp);

    _ = await dispatcher.LocalInvokeAsync<RpcResponse>(new RpcCommand(Guid.NewGuid()));

    await Assert.That(foreign.RoutingLookups).IsGreaterThanOrEqualTo(1)
      .Because("the cascade has to ask the declaring assembly how its receptor routes, rather "
             + "than assuming the host's default");
  }

}
