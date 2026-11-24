// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Unit.Dispatcher.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Deriving a locally invoked <c>[GenerateStreamId]</c> event's stream from the handling (#2552) costs nothing outside a
/// handling: a void local invoke that generates the event's stream every time (the default, <c>OnlyIfEmpty = false</c>)
/// allocates exactly what the same invoke allocates when it asks the same questions of the event and then generates
/// nothing (<c>OnlyIfEmpty = true</c> on an event that already names its stream). The difference between the two is the
/// stream generation the void path reaches outside a handling, so it must be zero. Measured on the current thread after
/// a warm-up call, as the dispatch policy's allocation tests are, so the only bytes counted are the invokes' own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/GeneratedStreamAssignments.cs</code-under-test>
[Category("Dispatcher")]
[NotInParallel("StreamIdExtractorRegistry")] // the process-wide stream id extractor registry, and the thread's allocation counter the measurement reads
public class DispatcherLocalInvokeGeneratedStreamAllocationTests {
  private const int ITERATIONS = 1_000;

  /// <summary>An event that starts a stream of its own.</summary>
  public class AllocationProbeStreamEvent : IEvent, IHasStreamId {
    /// <inheritdoc />
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }
  }

  /// <summary>The same event, generating only when it names no stream.</summary>
  public class AllocationProbeNamedStreamEvent : IEvent, IHasStreamId {
    /// <inheritdoc />
    [StreamId]
    [GenerateStreamId(OnlyIfEmpty = true)]
    public Guid StreamId { get; set; }
  }

  /// <summary>Handles the stream-starting event synchronously.</summary>
  public class AllocationProbeStreamEventReceptor : IReceptor<AllocationProbeStreamEvent> {
    /// <inheritdoc />
    public ValueTask HandleAsync(AllocationProbeStreamEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>Handles the event that names its stream synchronously.</summary>
  public class AllocationProbeNamedStreamEventReceptor : IReceptor<AllocationProbeNamedStreamEvent> {
    /// <inheritdoc />
    public ValueTask HandleAsync(AllocationProbeNamedStreamEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  [Test]
  public async Task LocalInvokeAsync_Void_GenerateStreamIdEvent_OutsideAHandling_GeneratingTheStreamAllocatesNothingAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();
    var streamEvent = new AllocationProbeStreamEvent();
    var namedEvent = new AllocationProbeNamedStreamEvent { StreamId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New() };
    var named = namedEvent.StreamId;

    var skipped = _measure(dispatcher, namedEvent, context);
    var generated = _measure(dispatcher, streamEvent, context);

    await Assert.That(namedEvent.StreamId).IsEqualTo(named)
      .Because("the baseline invokes asked for the policy and the stream and generated nothing");
    await Assert.That(streamEvent.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("the measured invokes generated the event's stream");
    await Assert.That(generated).IsEqualTo(skipped)
      .Because("outside a handling the stream is the instance's minted one, read back without allocating: the void path pays nothing for it");
  }

  /// <summary>Bytes the current thread allocates over <see cref="ITERATIONS"/> void invokes of one instance, after one warm-up invoke.</summary>
  private static long _measure(IDispatcher dispatcher, object message, IMessageContext context) {
    _invokeSynchronously(dispatcher, message, context);
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _invokeSynchronously(dispatcher, message, context);
    }
    return GC.GetAllocatedBytesForCurrentThread() - before;
  }

  private static void _invokeSynchronously(IDispatcher dispatcher, object message, IMessageContext context) {
    var pending = dispatcher.LocalInvokeAsync(message, context);
    if (!pending.IsCompletedSuccessfully) {
      throw new InvalidOperationException("The measured invoke must complete synchronously, on the thread being measured.");
    }
  }

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
