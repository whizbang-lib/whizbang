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

  // No default/fallback values. Every construction site must provide definitive Mode + Source.
  // Event store reads must reconstitute from stored metadata (requires DB schema to include DispatchContext).
  // Tests/benchmarks must use explicit values matching the scenario under test.
}
