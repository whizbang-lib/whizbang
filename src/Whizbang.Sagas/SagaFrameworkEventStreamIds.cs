// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Sagas;

/// <summary>
/// Resolves the stream of every saga event: the watchdog tick, the abandon event and the continuation
/// request the framework publishes itself, and every <see cref="ISagaStreamEvent"/>, which is every
/// event a <c>[Saga]</c> declaration generates. Each is stored on the stream of the saga it belongs to,
/// keyed by the saga id.
/// </summary>
/// <remarks>
/// <para>
/// A consumer's events get a generated extractor from their own assembly's source generator. These
/// types live in <c>Whizbang.Sagas</c>, which no generator runs over, so without this the dispatcher
/// found no stream for them and stored each on a stream named after its own message id. The
/// stranded-saga sweep, which looks for a pending tick by the saga's stream, then never saw one, and a
/// perspective could not apply the abandon event to the saga's row.
/// </para>
/// <para>
/// Registered with <see cref="StreamIdExtractorRegistry"/> when the assembly loads, beside the
/// extractors generated for every other assembly, so a host needs no call to get it. It answers only
/// for saga events and returns <see langword="null"/> for everything else, leaving consumer events to
/// their own extractors. The events a <c>[Saga]</c> declaration generates need it as much as the
/// framework's own: they are emitted by another source generator, which a consumer's stream id
/// generator cannot see, so before it they were each stored on a stream of their own message id.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaFrameworkEventStreamTests.cs</tests>
public sealed class SagaFrameworkEventStreamIds : IStreamIdExtractor {

  // CA2255: a library module initializer is the registration pattern generated extractors use too.
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  internal static void Register() => StreamIdExtractorRegistry.Register(new SagaFrameworkEventStreamIds(), priority: PRIORITY);

  /// <summary>
  /// Ahead of the extractors generated for consumer assemblies (100): a <c>[Saga&lt;TBase&gt;]</c> event
  /// base can carry a <c>[StreamId]</c> of its own, and a saga event belongs on its saga's stream
  /// whatever its base says.
  /// </summary>
  internal const int PRIORITY = 50;

  /// <inheritdoc />
  public Guid? ExtractStreamId(object message, Type messageType) => message switch {
    SagaCompletionWatchdogTickEvent tick => tick.StreamId,
    SagaCompletionAbandonedEvent abandoned => abandoned.StreamId,
    SagaContinuationRequestedEvent continuation => continuation.StreamId,
    ISagaStreamEvent sagaEvent => sagaEvent.SagaId,
    _ => null,
  };

  /// <inheritdoc />
  public bool SetStreamId(object message, Guid streamId) {
    switch (message) {
      case SagaCompletionWatchdogTickEvent tick:
        tick.StreamId = streamId;
        return true;
      case SagaCompletionAbandonedEvent abandoned:
        abandoned.StreamId = streamId;
        return true;
      case SagaContinuationRequestedEvent continuation:
        continuation.StreamId = streamId;
        return true;
      case ISagaStreamEvent sagaEvent:
        sagaEvent.SagaId = streamId;
        return true;
      default:
        return false;
    }
  }
}
