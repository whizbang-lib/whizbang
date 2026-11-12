// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.Core.Minting;

/// <summary>
/// Where a stream of events is cut into composites (#2285), bound from <c>Whizbang:CompositeBatches</c> for the
/// service-wide default. A batch is cut at whichever limit it reaches first: messages, bytes, or time.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Minting/CompositeBatchesTests.cs</tests>
public sealed class CompositeBatchOptions {
  /// <summary>The configuration section of the service-wide default.</summary>
  public const string SECTION = "Whizbang:CompositeBatches";

  /// <summary>
  /// The byte limit when none is set and none is derived: three quarters of the default body-offload threshold (64 KiB),
  /// leaving room for the composite's own envelope.
  /// </summary>
  public const long DEFAULT_MAX_BUFFERED_BYTES = 48L * 1024L;

  /// <summary>
  /// The most inner events one composite carries. Null (the default) is the composite's own
  /// <see cref="ICompositeEvent.MaxInnerEventsAllowed"/>; a larger value is lowered to it, so a batch is never a composite
  /// the dispatcher refuses.
  /// </summary>
  public int? MaxBufferedMessages { get; set; }

  /// <summary>
  /// The most serialized bytes of inner events one composite carries, measured with the framework's serializer. Null uses
  /// the service default, which the registration derives to stay under the transport's message size and the body-offload
  /// threshold (<see cref="DEFAULT_MAX_BUFFERED_BYTES"/> without one); 0 or less means no byte limit. An event larger than
  /// the limit is sent alone.
  /// </summary>
  public long? MaxBufferedBytes { get; set; }

  /// <summary>How long a batch waits for its next event before it is sent. Default 100 ms.</summary>
  public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(100);

  /// <summary>The longest a batch's first event waits before the batch is sent, however busy the source. Default 1 s.</summary>
  public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Streams events into composites (#2285): <see cref="ToCompositeBatches{TEvent,TComposite}"/> for code that wants the
/// batches, <c>PublishCompositeBatchesAsync</c> to publish each one.
/// </summary>
/// <remarks>
/// <para>
/// A batch is cut at whichever comes first: <see cref="CompositeBatchOptions.MaxBufferedMessages"/> events,
/// <see cref="CompositeBatchOptions.MaxBufferedBytes"/> serialized bytes, or time: the batch is sent once no event has
/// arrived for <see cref="CompositeBatchOptions.DebounceInterval"/>, and never later than
/// <see cref="CompositeBatchOptions.MaxWait"/> after its first event. An event larger than the byte limit is sent alone.
/// Events keep source order, and each inner event keeps its own stream id. The end of the source sends what remains.
/// </para>
/// <para>
/// No event read from the source is dropped: when the source throws, or the enumeration is canceled, the events already
/// read are sent as one last batch and then the exception surfaces.
/// </para>
/// <para>
/// The composite's cap is read once, from a composite the factory builds over no events, so the factory is called once
/// with an empty list before the first batch.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Minting/CompositeBatchesTests.cs</tests>
public static class CompositeBatchExtensions {
  /// <summary>Inert stub.</summary>
  public static IAsyncEnumerable<TComposite> ToCompositeBatches<TEvent, TComposite>(
      this IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider? timeProvider = null)
      where TEvent : IMessage
      where TComposite : ICompositeEvent =>
    ToCompositeBatches(source, factory, options, timeProvider, _ => 0);

  /// <summary>
  /// Cuts <paramref name="source"/> into composites and publishes each on the system clock; see the overload that takes a
  /// <see cref="TimeProvider"/>.
  /// </summary>
  /// <typeparam name="TEvent">The event type.</typeparam>
  /// <typeparam name="TComposite">The composite type.</typeparam>
  /// <param name="dispatcher">The dispatcher to publish with.</param>
  /// <param name="source">The events, in order.</param>
  /// <param name="factory">Builds one composite over a batch of events.</param>
  /// <param name="options">Where batches are cut; inject <c>IOptions&lt;CompositeBatchOptions&gt;</c> for the service default.</param>
  /// <param name="cancellationToken">Stops reading the source.</param>
  /// <returns>How many composites were published.</returns>
  public static Task<int> PublishCompositeBatchesAsync<TEvent, TComposite>(
      this IDispatcher dispatcher,
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      CancellationToken cancellationToken = default)
      where TEvent : IMessage
      where TComposite : ICompositeEvent =>
    PublishCompositeBatchesAsync(dispatcher, source, factory, options, TimeProvider.System, cancellationToken);

  /// <summary>Inert stub.</summary>
  public static Task<int> PublishCompositeBatchesAsync<TEvent, TComposite>(
      this IDispatcher dispatcher,
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider timeProvider,
      CancellationToken cancellationToken = default)
      where TEvent : IMessage
      where TComposite : ICompositeEvent {
    _ = (dispatcher, source, factory, options, cancellationToken, timeProvider);
    return Task.FromResult(0);
  }

  /// <summary>Inert stub.</summary>
  internal static IAsyncEnumerable<TComposite> ToCompositeBatches<TEvent, TComposite>(
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider? timeProvider,
      Func<TEvent, long> measure)
      where TComposite : ICompositeEvent {
    _ = (source, factory, options, timeProvider, measure);
    return AsyncEnumerable.Empty<TComposite>();
  }
}

/// <summary>Inert stub.</summary>
internal static class CompositeBatchSize {
  /// <summary>Inert stub.</summary>
  public static long Of(object evt, JsonSerializerOptions options) {
    _ = (evt, options);
    return 0;
  }
}
