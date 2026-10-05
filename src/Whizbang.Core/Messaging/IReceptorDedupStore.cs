// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Abstraction for tracking prior receptor invocations so <see cref="ReceptorInvoker"/>
/// can enforce "exactly once per receptor per message" before firing.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation (<see cref="EnvelopeReceptorDedupStore"/>) persists records
/// on the envelope itself via <see cref="IMessageEnvelope.ReceptorInvocations"/> — zero
/// external dependency, zero DB writes, and the records ride along with the message
/// through transport / inbox / outbox naturally.
/// </para>
/// <para>
/// A future database-backed implementation would key on
/// <see cref="IMessageEnvelope.MessageId"/> and receptor id — the interface is designed
/// so that swapping implementations is a DI registration change, not an API change in
/// <see cref="ReceptorInvoker"/>.
/// </para>
/// <para>
/// The interface uses <see cref="ValueTask"/> so synchronous (envelope-backed) and
/// asynchronous (DB-backed) implementations both return without allocating on the hot
/// path when the synchronous path dominates.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_RecordInvocationThenTryGet_ReturnsRecordedValueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_TryGetPriorInvocation_ReturnsNullForUnseenReceptorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_PriorInvocationIsReturnedRegardlessOfStageAsync</tests>
public interface IReceptorDedupStore {
  /// <summary>
  /// Returns a prior <see cref="ReceptorInvocationRecord"/> for <paramref name="receptorId"/>
  /// on the envelope's message, or null if the receptor has not fired for this message yet.
  /// </summary>
  /// <remarks>
  /// The check is per-receptor, not per-stage: a receptor that fired at
  /// <see cref="LifecycleStage.LocalImmediateInline"/> must be blocked from re-firing at
  /// <see cref="LifecycleStage.PreOutboxInline"/> for the same message unless it declares
  /// itself idempotent.
  /// </remarks>
  ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
    IMessageEnvelope envelope,
    string receptorId,
    CancellationToken cancellationToken);

  /// <summary>
  /// Returns a prior <see cref="ReceptorInvocationRecord"/> for <paramref name="receptorId"/> that
  /// <paramref name="serviceName"/> wrote, or null if the receptor has not fired for this message in
  /// that service.
  /// </summary>
  /// <remarks>
  /// A receptor runs once per message in each service, so a record another service wrote does not
  /// count. Service names compare without regard to case. A <see langword="null"/>
  /// <paramref name="serviceName"/> asks for a record from any service, as a receptor marked
  /// <see cref="ReceptorOnceAcrossServicesAttribute"/> does, and a record that names no service counts
  /// for every service, since it cannot be placed.
  /// </remarks>
  /// <docs>fundamentals/receptors/exactly-once-firing#once-per-service</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:TryGetPriorInvocationForService_RecordFromAnotherService_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:TryGetPriorInvocationForService_RecordFromThisService_ReturnsItAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:TryGetPriorInvocationForService_NoServiceName_ReturnsARecordFromAnyServiceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs:TryGetPriorInvocationForService_RecordNamingNoService_CountsForEveryServiceAsync</tests>
  ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
    IMessageEnvelope envelope,
    string receptorId,
    string? serviceName,
    CancellationToken cancellationToken);

  /// <summary>
  /// Records that a receptor has successfully fired for this envelope.
  /// </summary>
  /// <remarks>
  /// Called after the receptor delegate returns successfully. On exception the invoker
  /// intentionally does NOT record so a retry can re-fire.
  /// </remarks>
  ValueTask RecordInvocationAsync(
    IMessageEnvelope envelope,
    ReceptorInvocationRecord record,
    CancellationToken cancellationToken);
}
