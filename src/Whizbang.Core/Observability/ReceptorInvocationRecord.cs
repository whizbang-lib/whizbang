using System;
using System.Text.Json.Serialization;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// A per-receptor record of a single invocation, captured after the receptor delegate
/// returns successfully. Records ride along with the envelope as part of
/// <see cref="MessageEnvelope{TMessage}.ReceptorInvocations"/>, surviving transport and
/// inbox / outbox serialization.
/// </summary>
/// <remarks>
/// <para>
/// This is the unit of truth for the "exactly once per receptor per message" contract.
/// Before a receptor fires, <see cref="IReceptorDedupStore.TryGetPriorInvocationAsync{TMessage}(MessageEnvelope{TMessage}, string, System.Threading.CancellationToken)"/>
/// is consulted for a prior record; if one exists and the receptor is not declared
/// <see cref="ReceptorIdempotentAttribute">idempotent</see>, the invocation is skipped.
/// </para>
/// <para>
/// Records use short JSON property names to minimize payload overhead:
/// <c>r</c>=ReceptorId, <c>s</c>=Stage, <c>at</c>=CompletedAt, <c>du</c>=Duration, <c>sn</c>=ServiceName.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:RoundtripsFaithfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:RecordInvocation_AppendsToExistingListAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationsRoundtripTests.cs:ReceptorInvocations_SurviveEnvelopeSerializerRoundtripAsync</tests>
public sealed record ReceptorInvocationRecord {
  /// <summary>The receptor's canonical identifier (typically the fully-qualified class name).</summary>
  [JsonPropertyName("r")]
  public required string ReceptorId { get; init; }

  /// <summary>The lifecycle stage at which the receptor actually fired.</summary>
  [JsonPropertyName("s")]
  public required LifecycleStage Stage { get; init; }

  /// <summary>When the receptor completed (end of the finally block in the invoker).</summary>
  [JsonPropertyName("at")]
  public required DateTimeOffset CompletedAt { get; init; }

  /// <summary>How long the receptor took to run.</summary>
  [JsonPropertyName("du")]
  public required TimeSpan Duration { get; init; }

  /// <summary>The service that invoked the receptor — used to disambiguate re-delivery from cross-service flow.</summary>
  [JsonPropertyName("sn")]
  public required string ServiceName { get; init; }
}
