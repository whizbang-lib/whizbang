using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Immutable context describing how a message was dispatched.
/// Carried on the envelope so lifecycle stages can make routing decisions
/// (e.g., skip PreOutboxInline when LocalDispatch already fired).
/// </summary>
/// <remarks>
/// <para>
/// Added in envelope version 2. Version 1 envelopes that predate this field
/// use <see cref="Default"/> during deserialization for backward compatibility.
/// </para>
/// <para>
/// <strong>AOT-compatible:</strong> This is a plain record with no reflection.
/// All properties are known at compile time.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#dispatch-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageDispatchContextTests.cs</tests>
public sealed record MessageDispatchContext {
  /// <summary>
  /// The dispatch mode flags indicating which paths this message takes
  /// (Local, Outbox, Both, EventStoreOnly).
  /// </summary>
  [System.Text.Json.Serialization.JsonPropertyName("m")]
  public required DispatchModes Mode { get; init; }

  /// <summary>
  /// The origin of this message — whether it was dispatched locally,
  /// published to the outbox, or received from the inbox (transport).
  /// </summary>
  [System.Text.Json.Serialization.JsonPropertyName("s")]
  public required MessageSource Source { get; init; }

  /// <summary>
  /// True when this message is in its default dispatch path (cascade from receptor return).
  /// When true, only default-stage receptors fire. Explicit [FireAt] receptors are skipped
  /// by the GetUntypedReceptorPublisher — they fire later via ReceptorInvoker at their
  /// declared lifecycle stage.
  /// </summary>
  [System.Text.Json.Serialization.JsonPropertyName("d")]
  public bool IsDefaultDispatch { get; init; }

  /// <summary>
  /// Returns a copy of this context with <see cref="IsDefaultDispatch"/> set to true.
  /// Used by cascade paths to signal that only default-stage receptors should fire.
  /// </summary>
  public MessageDispatchContext WithDefaultDispatch() =>
    this with { IsDefaultDispatch = true };

  /// <summary>
  /// Sentinel context used by cascade paths when no source envelope is available.
  /// Has IsDefaultDispatch = true so that the publisher skips explicit [FireAt] receptors.
  /// </summary>
  internal static readonly MessageDispatchContext CascadeDefault = new() {
    Mode = Dispatch.DispatchModes.Local,
    Source = MessageSource.Local,
    IsDefaultDispatch = true
  };
}
