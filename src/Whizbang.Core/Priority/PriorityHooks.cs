using Whizbang.Core.Observability;

namespace Whizbang.Core.Priority;

/// <summary>What the producer hook sees when a message is dispatched.</summary>
/// <param name="Envelope">The outgoing envelope.</param>
/// <param name="MessageTypeName">The message type, rendered by the shared type-name helpers.</param>
/// <param name="DispatchContext">The dispatch context (mode, source, handler) when known.</param>
/// <param name="IsScheduled">Whether the message is scheduled for later delivery.</param>
/// <param name="ParentEffectivePriority">The effective number of the message being handled when this one was produced, or <see cref="WorkPriority.UNDECLARED"/> outside any handling.</param>
/// <param name="Declared">The number declared so far (by an explicit declaration or an earlier hook).</param>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public sealed record PriorityDeclarationContext(
  IMessageEnvelope Envelope,
  string MessageTypeName,
  MessageDispatchContext? DispatchContext,
  bool IsScheduled,
  int ParentEffectivePriority,
  int Declared);

/// <summary>What the receive hook sees before a message is stored in the inbox.</summary>
/// <param name="Declared">The number the producer declared (<see cref="WorkPriority.UNDECLARED"/> when none).</param>
/// <param name="Envelope">The received envelope.</param>
/// <param name="MessageTypeName">The message type, rendered by the shared type-name helpers.</param>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public sealed record PriorityReceiveContext(
  int Declared,
  IMessageEnvelope Envelope,
  string MessageTypeName);

/// <summary>
/// Declares a message's priority at dispatch. Hooks run in <see cref="Order"/>; each sees the number the
/// previous one declared in <see cref="PriorityDeclarationContext.Declared"/> and returns the number to
/// carry on. The framework's default, <see cref="ContextPriorityProducerHook"/>, derives from dispatch
/// context; a host's own hook replaces or refines it.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public interface IPriorityProducerHook {
  /// <summary>Position in the chain; lower runs first. The framework default runs at 1000.</summary>
  int Order { get; }

  /// <summary>Returns the declared number for this message.</summary>
  int DeclarePriority(PriorityDeclarationContext context);
}

/// <summary>
/// Decides the effective priority a consumer stores for a received message. Hooks run in <see cref="Order"/>;
/// each sees the previous answer as <see cref="PriorityReceiveContext.Declared"/>. Lowering and raising are
/// both allowed; the framework records both numbers. The default, <see cref="AcceptDeclaredPriorityReceiveHook"/>,
/// accepts the declared number and reads an undeclared one as <see cref="WorkPriority.STANDARD"/>.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public interface IPriorityReceiveHook {
  /// <summary>Position in the chain; lower runs first. The framework default runs at 1000.</summary>
  int Order { get; }

  /// <summary>Returns the effective number for this message on this consumer.</summary>
  int Classify(PriorityReceiveContext context);
}

/// <summary>One stream of a claimed batch as the batch hook sees it.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="FoldedPriority">The stream's priority folded from its rows in the batch.</param>
/// <param name="OldestAge">Age of the stream's oldest pending row.</param>
/// <param name="PendingRows">Rows of the stream in the batch.</param>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public sealed record PriorityBatchEntry(Guid StreamId, int FoldedPriority, TimeSpan OldestAge, int PendingRows);

/// <summary>
/// Adjusts stream priorities over a claimed batch before dispatch order is decided: decay with age and bump
/// back when related work arrives, hold a stream while another is in flight, or any rule the defaults never
/// anticipated. A hook sets a stream's number, never a row's position, so per-stream order stays an
/// invariant no hook can break.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
public interface IPriorityBatchHook {
  /// <summary>Position in the chain; lower runs first.</summary>
  int Order { get; }

  /// <summary>Returns the stream's number for this batch, given its folded number and the batch around it.</summary>
  int Adjust(PriorityBatchEntry stream, IReadOnlyList<PriorityBatchEntry> batch);
}

/// <summary>
/// The framework's producer default: an application-initiated dispatch (outside any handler) is interactive,
/// scheduled work is background, everything else is standard, and a child inherits its parent's effective
/// number, never rising above it by inheritance alone. An explicit declaration made earlier is kept.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityHooksTests.cs</tests>
public sealed class ContextPriorityProducerHook : IPriorityProducerHook {
  /// <inheritdoc />
  public int Order => 1000;

  /// <inheritdoc />
  public int DeclarePriority(PriorityDeclarationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    if (WorkPriority.IsDeclared(context.Declared)) {
      return context.Declared;   // an explicit declaration (or an earlier hook's) is kept
    }
    if (context.IsScheduled) {
      return WorkPriority.BACKGROUND;   // nobody waits on work scheduled for later, whatever produced it
    }
    if (WorkPriority.IsDeclared(context.ParentEffectivePriority)) {
      return context.ParentEffectivePriority;   // inheritance: the parent's number, exactly
    }
    // Application-initiated (no handler on the dispatch context) means a caller waits at a boundary.
    return context.DispatchContext?.HandlerName is null ? WorkPriority.INTERACTIVE : WorkPriority.STANDARD;
  }
}

/// <summary>The framework's receive default: accept the declared number; an undeclared one is standard.</summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityHooksTests.cs</tests>
public sealed class AcceptDeclaredPriorityReceiveHook : IPriorityReceiveHook {
  /// <inheritdoc />
  public int Order => 1000;

  /// <inheritdoc />
  public int Classify(PriorityReceiveContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return WorkPriority.Effective(context.Declared);
  }
}

/// <summary>Runs the registered hooks of each kind in order, threading each answer to the next.</summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityHooksTests.cs</tests>
public sealed class PriorityHookChain {
  private readonly IPriorityProducerHook[] _producerHooks;
  private readonly IPriorityReceiveHook[] _receiveHooks;
  private readonly IPriorityBatchHook[] _batchHooks;

  /// <summary>Creates the chain from the registered hooks (any order; sorted once).</summary>
  public PriorityHookChain(IEnumerable<IPriorityProducerHook> producerHooks, IEnumerable<IPriorityReceiveHook> receiveHooks, IEnumerable<IPriorityBatchHook> batchHooks) {
    ArgumentNullException.ThrowIfNull(producerHooks);
    ArgumentNullException.ThrowIfNull(receiveHooks);
    ArgumentNullException.ThrowIfNull(batchHooks);
    _producerHooks = [.. producerHooks.OrderBy(h => h.Order)];
    _receiveHooks = [.. receiveHooks.OrderBy(h => h.Order)];
    _batchHooks = [.. batchHooks.OrderBy(h => h.Order)];
  }

  /// <summary>Whether no hook of any kind is registered.</summary>
  public bool IsEmpty => _producerHooks.Length == 0 && _receiveHooks.Length == 0 && _batchHooks.Length == 0;

  /// <summary>Runs the producer hooks; returns the declared number.</summary>
  public int DeclarePriority(PriorityDeclarationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    var declared = context.Declared;
    foreach (var hook in _producerHooks) {
      declared = hook.DeclarePriority(context with { Declared = declared });
    }
    return declared;
  }

  /// <summary>Runs the receive hooks; returns the effective number.</summary>
  public int Classify(PriorityReceiveContext context) {
    ArgumentNullException.ThrowIfNull(context);
    var effective = context.Declared;
    foreach (var hook in _receiveHooks) {
      effective = hook.Classify(context with { Declared = effective });
    }
    return effective;
  }

  /// <summary>Runs the batch hooks for one stream; returns its number for this batch.</summary>
  public int Adjust(PriorityBatchEntry stream, IReadOnlyList<PriorityBatchEntry> batch) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(batch);
    var priority = stream.FoldedPriority;
    foreach (var hook in _batchHooks) {
      priority = hook.Adjust(stream with { FoldedPriority = priority }, batch);
    }
    return priority;
  }
}

/// <summary>
/// The ambient effective priority of the message being handled, so anything produced during that handling
/// can inherit it. Set by the dispatch workers for the duration of a handler; flows with the async context
/// into the work a handler starts.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityHooksTests.cs</tests>
public static class PriorityContext {
  private static readonly AsyncLocal<int> _parent = new();

  /// <summary>The effective number of the message currently being handled, or <see cref="WorkPriority.UNDECLARED"/> outside any handling.</summary>
  public static int CurrentParent => _parent.Value;

  /// <summary>Marks the start of handling a message with <paramref name="effectivePriority"/>; disposing restores the previous value.</summary>
  public static IDisposable Enter(int effectivePriority) {
    var previous = _parent.Value;
    _parent.Value = effectivePriority;
    return new Scope(previous);
  }

  private sealed class Scope(int previous) : IDisposable {
    public void Dispose() => _parent.Value = previous;
  }
}
