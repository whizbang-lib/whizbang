using System;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown from <see cref="ReceptorInvoker"/> when the double-fire guardrail is configured
/// with <c>OnDoubleFire = Throw</c> and a receptor is about to be invoked a second time
/// for the same message.
/// </summary>
/// <remarks>
/// Carries enough context to diagnose the duplicate without re-walking the envelope: the
/// receptor id, the stage the duplicate attempt was on, the stage at which the prior
/// invocation fired, and the message id.
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public sealed class DuplicateReceptorFireException : Exception {
  /// <summary>The receptor that was about to be invoked a second time.</summary>
  public string ReceptorId { get; }
  /// <summary>The stage the invoker was attempting to fire on when the duplicate was detected.</summary>
  public LifecycleStage CurrentStage { get; }
  /// <summary>The stage at which this receptor previously fired for this message.</summary>
  public LifecycleStage PriorStage { get; }
  /// <summary>The message's identifier.</summary>
  public Guid MessageId { get; }
  /// <summary>The prior invocation record. Present when the envelope carried one; null if the store returned only a flag.</summary>
  public ReceptorInvocationRecord? PriorInvocation { get; }

  /// <summary>Creates a new exception describing a detected double-fire attempt.</summary>
  public DuplicateReceptorFireException(
    string receptorId,
    LifecycleStage currentStage,
    LifecycleStage priorStage,
    Guid messageId,
    ReceptorInvocationRecord? priorInvocation)
    : base($"Receptor '{receptorId}' was about to fire at {currentStage} for message {messageId} but already fired at {priorStage}.") {
    ReceptorId = receptorId;
    CurrentStage = currentStage;
    PriorStage = priorStage;
    MessageId = messageId;
    PriorInvocation = priorInvocation;
  }

  /// <summary>Parameterless constructor for compatibility with <c>Exception</c> conventions.</summary>
  public DuplicateReceptorFireException()
    : base("A receptor attempted to fire a second time for the same message.") {
    ReceptorId = string.Empty;
  }

  /// <summary>Constructor accepting a message for compatibility with <c>Exception</c> conventions.</summary>
  public DuplicateReceptorFireException(string message)
    : base(message) {
    ReceptorId = string.Empty;
  }

  /// <summary>Constructor accepting a message and inner exception for compatibility with <c>Exception</c> conventions.</summary>
  public DuplicateReceptorFireException(string message, Exception innerException)
    : base(message, innerException) {
    ReceptorId = string.Empty;
  }
}
