// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Serialization;

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
  /// <summary>Cuts <paramref name="source"/> into composites built by <paramref name="factory"/>.</summary>
  /// <typeparam name="TEvent">The event type.</typeparam>
  /// <typeparam name="TComposite">The composite type.</typeparam>
  /// <param name="source">The events, in order.</param>
  /// <param name="factory">Builds one composite over a batch of events.</param>
  /// <param name="options">Where batches are cut.</param>
  /// <param name="timeProvider">Drives the debounce and the maximum wait; null uses the system clock.</param>
  /// <returns>The composites, in order. Cancel with <c>WithCancellation</c>.</returns>
  public static IAsyncEnumerable<TComposite> ToCompositeBatches<TEvent, TComposite>(
      this IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider? timeProvider = null)
      where TEvent : IMessage
      where TComposite : ICompositeEvent {
    var serializer = JsonContextRegistry.CreateCombinedOptions();
    return ToCompositeBatches(source, factory, options, timeProvider, evt => CompositeBatchSize.Of(evt, serializer));
  }

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

  /// <summary>
  /// Cuts <paramref name="source"/> into composites and publishes each, awaiting it, so the bounded outbox buffer holds
  /// the producer back. A composite already built is published even when <paramref name="cancellationToken"/> is canceled,
  /// so no event read from the source is dropped.
  /// </summary>
  /// <typeparam name="TEvent">The event type.</typeparam>
  /// <typeparam name="TComposite">The composite type.</typeparam>
  /// <param name="dispatcher">The dispatcher to publish with.</param>
  /// <param name="source">The events, in order.</param>
  /// <param name="factory">Builds one composite over a batch of events.</param>
  /// <param name="options">Where batches are cut; inject <c>IOptions&lt;CompositeBatchOptions&gt;</c> for the service default.</param>
  /// <param name="timeProvider">Drives the debounce and the maximum wait.</param>
  /// <param name="cancellationToken">Stops reading the source.</param>
  /// <returns>How many composites were published.</returns>
  public static async Task<int> PublishCompositeBatchesAsync<TEvent, TComposite>(
      this IDispatcher dispatcher,
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider timeProvider,
      CancellationToken cancellationToken = default)
      where TEvent : IMessage
      where TComposite : ICompositeEvent {
    ArgumentNullException.ThrowIfNull(dispatcher);
    var published = 0;
    await foreach (var composite in source.ToCompositeBatches(factory, options, timeProvider)
                     .WithCancellation(cancellationToken).ConfigureAwait(false)) {
      await dispatcher.PublishAsync(composite, DispatchOptions.Default).ConfigureAwait(false);
      published++;
    }
    return published;
  }

  /// <summary>The batching, with the size of an event measured by <paramref name="measure"/>.</summary>
  internal static IAsyncEnumerable<TComposite> ToCompositeBatches<TEvent, TComposite>(
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider? timeProvider,
      Func<TEvent, long> measure)
      where TComposite : ICompositeEvent {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(factory);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DebounceInterval, TimeSpan.Zero);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxWait, TimeSpan.Zero);
    return _batchAsync(source, factory, options, timeProvider ?? TimeProvider.System, measure, default);
  }

  private static async IAsyncEnumerable<TComposite> _batchAsync<TEvent, TComposite>(
      IAsyncEnumerable<TEvent> source,
      Func<IReadOnlyList<TEvent>, TComposite> factory,
      CompositeBatchOptions options,
      TimeProvider time,
      Func<TEvent, long> measure,
      [EnumeratorCancellation] CancellationToken cancellationToken)
      where TComposite : ICompositeEvent {
    var cap = factory([]).MaxInnerEventsAllowed;
    var maxMessages = Math.Max(1, Math.Min(options.MaxBufferedMessages ?? cap, cap));
    var maxBytes = options.MaxBufferedBytes ?? CompositeBatchOptions.DEFAULT_MAX_BUFFERED_BYTES;
    var batch = new List<TEvent>();
    long bytes = 0;
    long firstArrival = 0;
    long lastArrival = 0;
    Task<bool>? pending = null;
    ExceptionDispatchInfo? failure = null;
    // The source is read through a token of its own, so a read still outstanding when the consumer stops early can be
    // ended before the source is disposed (disposing an enumerator with a read in flight is not allowed).
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var enumerator = source.GetAsyncEnumerator(stop.Token);
    try {
      while (true) {
        pending ??= enumerator.MoveNextAsync().AsTask();
        if (batch.Count > 0 && !pending.IsCompleted) {
          var due = _due(time, firstArrival, lastArrival, options);
          if (due > TimeSpan.Zero && !await _arrivesWithinAsync(pending, due, time).ConfigureAwait(false)) {
            due = TimeSpan.Zero;
          }
          if (due <= TimeSpan.Zero && !pending.IsCompleted) {
            // The window closed with no new event: the batch goes, and the read already started is kept for the next.
            var quiet = _cut(factory, batch);
            bytes = 0;
            yield return quiet;
            continue;
          }
        }
        bool moved;
        try {
          moved = await pending.ConfigureAwait(false);
        } catch (Exception ex) {
          failure = ExceptionDispatchInfo.Capture(ex);
          moved = false;
        }
        pending = null;
        if (!moved) {
          break;
        }
        var evt = enumerator.Current;
        var size = maxBytes > 0 ? measure(evt) : 0;
        if (batch.Count > 0 && (batch.Count + 1 > maxMessages || (maxBytes > 0 && bytes + size > maxBytes))) {
          var full = _cut(factory, batch);
          bytes = 0;
          yield return full;
        }
        if (batch.Count == 0) {
          firstArrival = time.GetTimestamp();
        }
        batch.Add(evt);
        bytes += size;
        lastArrival = time.GetTimestamp();
        if (batch.Count >= maxMessages || (maxBytes > 0 && bytes >= maxBytes)) {
          var reached = _cut(factory, batch);
          bytes = 0;
          yield return reached;
        }
      }
      if (batch.Count > 0) {
        // The source ended, failed or was canceled: what was read is sent, never dropped.
        yield return _cut(factory, batch);
      }
    } finally {
      if (pending is not null) {
        await stop.CancelAsync().ConfigureAwait(false);
        try {
          await pending.ConfigureAwait(false);
        } catch (OperationCanceledException) {
          // The read was ended by the stop above: the consumer stopped before the source's next event arrived.
        }
      }
      await enumerator.DisposeAsync().ConfigureAwait(false);
    }
    failure?.Throw();
  }

  /// <summary>How long until the batch is due: the debounce after the last event, capped by the wait after the first.</summary>
  private static TimeSpan _due(TimeProvider time, long firstArrival, long lastArrival, CompositeBatchOptions options) {
    var debounce = options.DebounceInterval - time.GetElapsedTime(lastArrival);
    var maxWait = options.MaxWait - time.GetElapsedTime(firstArrival);
    return debounce < maxWait ? debounce : maxWait;
  }

  /// <summary>
  /// Waits for the pending read or for <paramref name="due"/> to pass, whichever is first; true when the read completed.
  /// A read that completes as the window closes is taken, never lost: the caller checks it before cutting.
  /// </summary>
  private static async Task<bool> _arrivesWithinAsync(Task<bool> pending, TimeSpan due, TimeProvider time) {
    using var timer = new CancellationTokenSource();
    var window = Task.Delay(due, time, timer.Token);
    var first = await Task.WhenAny(pending, window).ConfigureAwait(false);
    await timer.CancelAsync().ConfigureAwait(false);
    return first == pending;
  }

  private static TComposite _cut<TEvent, TComposite>(Func<IReadOnlyList<TEvent>, TComposite> factory, List<TEvent> batch) {
    var composite = factory([.. batch]);
    batch.Clear();
    return composite;
  }
}

/// <summary>The size of an event as the framework serializes it, for the byte limit of a composite batch.</summary>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Minting/CompositeBatchesTests.cs</tests>
internal static class CompositeBatchSize {
  /// <summary>The UTF-8 JSON length of <paramref name="evt"/>, through its registered type info (no reflection).</summary>
  /// <exception cref="InvalidOperationException">The event's type has no registered JSON type info.</exception>
  public static long Of(object evt, JsonSerializerOptions options) {
    var type = evt.GetType();
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(TypeNameFormatter.AssemblyQualifiedNameOrDisplay(type), options)
      ?? throw new InvalidOperationException(
        $"No JSON type info found for {TypeNameFormatter.DisplayName(type)}, so its size cannot be measured. Ensure the type is registered in a JsonSerializerContext.");
    return JsonSerializer.SerializeToUtf8Bytes(evt, typeInfo).Length;
  }
}
