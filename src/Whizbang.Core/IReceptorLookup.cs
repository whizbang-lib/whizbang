using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core;

/// <summary>
/// One assembly's compile-time receptor routing tables, exposed as a composable lookup. Receptor
/// discovery is source-only, so each assembly's generated dispatcher can route ONLY the receptors
/// declared in that assembly — and every Whizbang-compiled assembly registers exactly one
/// dispatcher, of which DI resolves the last. Without composition, every other assembly's
/// receptors are silently unreachable: a message dispatched to one simply never runs, with no
/// exception and no diagnostic (issue #491).
/// </summary>
/// <remarks>
/// Every assembly's generated dispatcher implements this and is additionally registered as an
/// <see cref="IReceptorLookup"/> (<c>TryAddEnumerable</c>, one per generated type). The winning
/// <see cref="Dispatcher"/> consults its own tables first and falls back to the FOREIGN lookups —
/// first match for send-style invocations, fan-out to all for publishes, because an event's
/// receptors may live in several assemblies at once. Zero reflection: the lookups are the same
/// compile-time tables the dispatchers already carry.
/// </remarks>
/// <docs>fundamentals/receptors/receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherForeignLookupTests.cs</tests>
public interface IReceptorLookup {
  /// <summary>This assembly's invoker for a non-void async receptor, or null when it declares none.</summary>
  ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>This assembly's invoker for a void async receptor, or null when it declares none.</summary>
  VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType);

  /// <summary>
  /// This assembly's type-erased publisher for an event's receptors, or null when it declares
  /// none. Publishes compose across assemblies: the base dispatcher invokes EVERY foreign
  /// assembly's publisher, not the first.
  /// </summary>
  Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType);

  /// <summary>This assembly's invoker for a non-void sync receptor, or null when it declares none.</summary>
  SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>This assembly's invoker for a void sync receptor, or null when it declares none.</summary>
  VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType);

  /// <summary>This assembly's type-erased any-receptor invoker, or null when it declares none.</summary>
  Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType);

  /// <summary>This assembly's [DefaultRouting] declaration for the message type, or null.</summary>
  Dispatch.DispatchModes? LookupReceptorDefaultRouting(Type messageType);
}
