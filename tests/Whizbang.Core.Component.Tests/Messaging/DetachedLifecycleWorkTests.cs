// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The detached lifecycle stages run in the background of the flush that starts them. <see cref="DetachedLifecycleWork"/>
/// is what a stopping host waits on so that none of them is still running when the service provider is disposed.
/// </summary>
[Category("Messaging")]
[Category("Lifecycle")]
public class DetachedLifecycleWorkTests {

  [Test]
  public async Task WithNothingInFlight_DrainCompletesAtOnceAsync() {
    var work = new DetachedLifecycleWork();

    var drain = work.DrainAsync(CancellationToken.None);

    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("a host with no detached work in flight has nothing to wait for");
  }

  [Test]
  public async Task Drain_WaitsForEveryTrackedInvocation_AndForgetsThemOnceFinishedAsync() {
    var work = new DetachedLifecycleWork();
    var first = new TaskCompletionSource();
    var second = new TaskCompletionSource();
    work.Track(first.Task);
    work.Track(second.Task);

    var drain = work.DrainAsync(CancellationToken.None);
    first.SetResult();

    await Assert.That(drain.IsCompleted).IsFalse()
      .Because("one invocation is still running");
    await Assert.That(work.InFlightCount).IsEqualTo(1)
      .Because("a finished invocation is no longer counted");

    second.SetResult();
    await drain;

    await Assert.That(work.InFlightCount).IsEqualTo(0);
  }

  [Test]
  public async Task AnInvocationThatFailedOrWasCanceled_DoesNotFailTheDrainAsync() {
    var work = new DetachedLifecycleWork();
    var failing = new TaskCompletionSource();
    var canceled = new TaskCompletionSource();
    work.Track(failing.Task);
    work.Track(canceled.Task);

    var drain = work.DrainAsync(CancellationToken.None);
    failing.SetException(new InvalidOperationException("receptor failed"));
    canceled.SetCanceled();

    await Assert.That(async () => await drain).ThrowsNothing()
      .Because("an invocation reports its own failure; the host stopping is not where it surfaces");
    await Assert.That(work.InFlightCount).IsEqualTo(0);
  }

  [Test]
  public async Task WorkTrackedWhileDraining_IsWaitedFor_TooAsync() {
    var work = new DetachedLifecycleWork();
    var first = new TaskCompletionSource();
    var late = new TaskCompletionSource();
    work.Track(first.Task);

    var drain = work.DrainAsync(CancellationToken.None);
    // A flush still finishing starts one more detached stage just as the first one ends.
    work.Track(late.Task);
    first.SetResult();

    await Assert.That(drain.IsCompleted).IsFalse()
      .Because("work started while the host waited is waited for as well");

    late.SetResult();
    await drain;

    await Assert.That(work.InFlightCount).IsEqualTo(0);
  }

  [Test]
  public async Task ACanceledDrain_StopsWaiting_AndLeavesTheWorkTrackedAsync() {
    var work = new DetachedLifecycleWork();
    var stuck = new TaskCompletionSource();
    work.Track(stuck.Task);
    using var deadline = new CancellationTokenSource();

    var drain = work.DrainAsync(deadline.Token);
    await deadline.CancelAsync();

    await Assert.That(async () => await drain).Throws<OperationCanceledException>()
      .Because("a shutdown that ran out of time is the host's to see");
    await Assert.That(work.InFlightCount).IsEqualTo(1)
      .Because("the invocation is still running");
    stuck.SetResult();
  }

  [Test]
  public async Task TheDistributeStages_TrackTheirDetachedStage_InTheContextsWorkAsync() {
    var work = new DetachedLifecycleWork();
    var receptor = new HeldDetachedReceptor();
    var context = _context(receptor, work);

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);
    await receptor.Entered.Task;

    await Assert.That(work.InFlightCount).IsEqualTo(1)
      .Because("the detached stage is running in the background, and the host must be able to wait for it");

    receptor.Release();
    await work.DrainAsync(CancellationToken.None);

    await Assert.That(receptor.DetachedStagesFinished).IsEqualTo(1);
  }

  [Test]
  public async Task AnAsyncOnlyStage_IsTrackedInTheContextsWorkAsync() {
    var work = new DetachedLifecycleWork();
    var receptor = new HeldDetachedReceptor();
    var context = _context(receptor, work);

    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(LifecycleStage.DistributeDetached, context);
    await receptor.Entered.Task;

    await Assert.That(work.InFlightCount).IsEqualTo(1)
      .Because("the detached stage is running in the background, and the host must be able to wait for it");

    receptor.Release();
    await work.DrainAsync(CancellationToken.None);

    await Assert.That(receptor.DetachedStagesFinished).IsEqualTo(1);
  }

  private static DistributeLifecycleContext _context(IReceptorInvoker receptor, DetachedLifecycleWork work) {
    var services = new ServiceCollection();
    services.AddSingleton(receptor);
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    return new DistributeLifecycleContext(
      [_outboxMessage()], [], scopeFactory, new PassThroughDeserializer(), Logger: null,
      EnableLifecycleTracing: false, DetachedWork: work);
  }

  private static OutboxMessage _outboxMessage() {
    var messageId = MessageId.New();
    return new OutboxMessage {
      MessageId = messageId.Value,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }

  /// <summary>Holds every detached stage until released; inline stages pass straight through.</summary>
  private sealed class HeldDetachedReceptor : IReceptorInvoker {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _detachedStagesFinished;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DetachedStagesFinished => Volatile.Read(ref _detachedStagesFinished);
    public void Release() => _release.TrySetResult();

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      if (stage is LifecycleStage.PostDistributeDetached or LifecycleStage.DistributeDetached) {
        Entered.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        Interlocked.Increment(ref _detachedStagesFinished);
      }
    }
  }

  private sealed class PassThroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => JsonElement.Parse(jsonBytes);
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }
}
