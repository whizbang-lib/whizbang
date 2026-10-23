// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The rollback publisher's not-ready hold keeps its rows (#1606). While the transport reports not ready,
/// <see cref="OutboxPublishWorker"/> re-buffers the rows it holds; their leases must be renewed through the work
/// coordinator, or they lapse, another claim takes them, and each re-claim charges an attempt, which is what the
/// hold exists to prevent. The lease renewal channel cannot do it: it drops any id with no registered lease
/// handle, and a held row has none.
/// </summary>
/// <remarks>
/// Every readiness check waits for the test's permission, so each pass of the hold runs at a point the test
/// chose, and the clock is a <see cref="FakeTimeProvider"/> the test advances between passes. The coordinator
/// (<see cref="HeldLeaseCoordinator"/>) keeps each row's lease expiry on that clock and records a renewal that
/// arrived after the lease had lapsed.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxPublishWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerHoldRenewalTests {
  private const int LEASE_SECONDS = 120;
  private const int ATTEMPTS = 3;
  private static readonly TimeSpan _signal = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(LEASE_SECONDS);

  /// <summary>
  /// A row held past one lease length keeps its lease: the hold renews it through the coordinator at once and
  /// again each time half a lease has passed, never after it lapsed, and charges no attempt. A pass inside the
  /// half lease renews nothing. Once the transport is ready the row publishes with its attempt count unchanged.
  /// </summary>
  [Test]
  public async Task Singular_HeldPastOneLeaseLength_RenewsTheLeaseAndChargesNoAttemptAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var start = clock.GetUtcNow();
    var work = _work();
    var coordinator = new HeldLeaseCoordinator(clock, _lease);
    coordinator.Claim(work.MessageId);
    var strategy = new SteppedReadinessStrategy(bulk: false);
    var fx = _build(strategy, coordinator, clock);

    using var cts = new CancellationTokenSource();
    try {
      await fx.Worker.StartAsync(cts.Token);
      await fx.Channel.WriteAsync(work, cts.Token);

      // Pass 1: not ready. The row is renewed at once.
      await strategy.PassAsync(ready: false);
      await coordinator.WaitForRenewalsAsync(1);

      // Pass 2, inside half a lease: nothing is due. Pass 3 entering proves pass 2 finished.
      clock.Advance(_lease / 2 - TimeSpan.FromSeconds(1));
      await strategy.PassAsync(ready: false);
      await strategy.WaitForCheckAsync();
      await Assert.That(coordinator.Renewals).Count().IsEqualTo(1)
        .Because("a lease renewed less than half a lease ago is not renewed again on every pass");

      // Passes 3 and 4, each after more than half a lease: the hold now spans more than one lease length.
      // Pass 3's check was already waited for above, so it is answered directly.
      clock.Advance(TimeSpan.FromSeconds(2));
      strategy.Answer(ready: false);
      await coordinator.WaitForRenewalsAsync(2);
      clock.Advance(_lease / 2 + TimeSpan.FromSeconds(1));
      await strategy.PassAsync(ready: false);
      await coordinator.WaitForRenewalsAsync(3);

      var heldFor = clock.GetUtcNow() - start;
      await Assert.That(heldFor).IsGreaterThan(_lease)
        .Because("the row was held for longer than one lease length");
      await Assert.That(coordinator.Lapsed).IsEmpty()
        .Because("every renewal arrived before the lease it extended had lapsed, so no other claim could take the row");
      await Assert.That(coordinator.ExpiryOf(work.MessageId)).IsGreaterThan(clock.GetUtcNow())
        .Because("the held row still holds a live lease");
      foreach (var renewal in coordinator.Renewals) {
        await Assert.That(renewal.Category).IsEqualTo(WorkCategory.Outbox);
        await Assert.That(renewal.Ids).IsEquivalentTo([work.MessageId]);
        await Assert.That(renewal.LeaseSeconds).IsEqualTo(LEASE_SECONDS)
          .Because("a renewal extends the lease by the configured renewal length");
      }
      await Assert.That(fx.Failure.All).IsEmpty()
        .Because("a held row is not a failed publish; reporting one would release it and its re-claim would charge an attempt");
      await Assert.That(strategy.Published).IsEmpty();

      // Ready: the held row publishes, still at the attempt count it was claimed with.
      await strategy.PassAsync(ready: true);
      await fx.Completion.WaitForCountAsync(1);
      await Assert.That(strategy.Published.Single().MessageId).IsEqualTo(work.MessageId);
      await Assert.That(strategy.Published.Single().Attempts).IsEqualTo(ATTEMPTS)
        .Because("the hold charged no attempt");
      await Assert.That(fx.Failure.All).IsEmpty();
    } finally {
      await cts.CancelAsync();
      await fx.Worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// The bulk path holds a whole batch: every row in it is renewed in one coordinator call, at once and again
  /// once half a lease has passed, and the batch publishes once the transport is ready.
  /// </summary>
  [Test]
  public async Task Bulk_HeldPastOneLeaseLength_RenewsEveryRowOfTheBatchAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var first = _work();
    var second = _work();
    var coordinator = new HeldLeaseCoordinator(clock, _lease);
    coordinator.Claim(first.MessageId);
    coordinator.Claim(second.MessageId);
    var strategy = new SteppedReadinessStrategy(bulk: true);
    var fx = _build(strategy, coordinator, clock);
    // Queued before the start, so the first bulk read takes both rows as one batch.
    fx.Channel.TryWrite(first);
    fx.Channel.TryWrite(second);

    using var cts = new CancellationTokenSource();
    try {
      await fx.Worker.StartAsync(cts.Token);

      await strategy.PassAsync(ready: false);
      await coordinator.WaitForRenewalsAsync(1);
      clock.Advance(_lease / 2 + TimeSpan.FromSeconds(1));
      await strategy.PassAsync(ready: false);
      await coordinator.WaitForRenewalsAsync(2);
      clock.Advance(_lease / 2 + TimeSpan.FromSeconds(1));
      await strategy.PassAsync(ready: false);
      await coordinator.WaitForRenewalsAsync(3);

      await Assert.That(coordinator.Lapsed).IsEmpty()
        .Because("held past one lease length, neither row's lease lapsed");
      foreach (var renewal in coordinator.Renewals) {
        await Assert.That(renewal.Ids).IsEquivalentTo([first.MessageId, second.MessageId])
          .Because("every row of the held batch is renewed, in one call");
      }
      await Assert.That(fx.Failure.All).IsEmpty();

      await strategy.PassAsync(ready: true);
      await fx.Completion.WaitForCountAsync(2);
      await Assert.That(strategy.Published.Select(w => w.MessageId)).IsEquivalentTo([first.MessageId, second.MessageId]);
      await Assert.That(strategy.Published.All(w => w.Attempts == ATTEMPTS)).IsTrue()
        .Because("the hold charged no attempt");
    } finally {
      await cts.CancelAsync();
      await fx.Worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// A renewal that fails (the database is unreachable for a moment) does not turn the hold into a failed
  /// publish: nothing is reported to the failure channel, which would release the row and charge an attempt on
  /// its re-claim. The row stays held, and the next pass renews it.
  /// </summary>
  [Test]
  public async Task Held_RenewalFails_KeepsHoldingAndRenewsOnTheNextPassAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var work = _work();
    var coordinator = new HeldLeaseCoordinator(clock, _lease) { FailingRenewals = 1 };
    coordinator.Claim(work.MessageId);
    var strategy = new SteppedReadinessStrategy(bulk: false);
    var fx = _build(strategy, coordinator, clock);

    using var cts = new CancellationTokenSource();
    try {
      await fx.Worker.StartAsync(cts.Token);
      await fx.Channel.WriteAsync(work, cts.Token);

      // Pass 1: the renewal throws. Pass 2 entering proves pass 1 finished and the row was kept.
      await strategy.PassAsync(ready: false);
      await strategy.WaitForCheckAsync();
      await Assert.That(coordinator.Renewals).IsEmpty();
      await Assert.That(fx.Failure.All).IsEmpty()
        .Because("a failed renewal is not a failed publish, and reporting one would charge the row an attempt");

      // Pass 2, at the same instant: the renewal that failed is still due, so it is made now.
      strategy.Answer(ready: false);
      await coordinator.WaitForRenewalsAsync(1);
      await Assert.That(coordinator.Renewals.Single().Ids).IsEquivalentTo([work.MessageId]);

      await strategy.PassAsync(ready: true);
      await fx.Completion.WaitForCountAsync(1);
      await Assert.That(strategy.Published.Single().Attempts).IsEqualTo(ATTEMPTS);
      await Assert.That(fx.Failure.All).IsEmpty();
    } finally {
      await cts.CancelAsync();
      await fx.Worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// A renewal in flight when the host stops ends with the worker: the shutdown is not logged as a failed
  /// renewal and not reported as a failed publish, and the worker stops cleanly.
  /// </summary>
  [Test]
  public async Task Held_StoppedDuringRenewal_StopsCleanlyWithoutReportingAFailureAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new HeldLeaseCoordinator(clock, _lease) { BlockRenewalsUntilCanceled = entered };
    var strategy = new SteppedReadinessStrategy(bulk: false);
    var fx = _build(strategy, coordinator, clock);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(), cts.Token);
    await strategy.PassAsync(ready: false);
    await entered.Task.WaitAsync(_signal);

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(fx.Worker.ExecuteTask!.IsCompleted).IsTrue();
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("a renewal canceled by shutdown ends the worker, it does not fault it");
    await Assert.That(fx.Failure.All).IsEmpty()
      .Because("a shutdown is not a failed publish");
  }

  // ── harness ──────────────────────────────────────────────────────────────────────────────────

  private sealed record Fixture(
    OutboxPublishWorker Worker, WorkChannel Channel, CompletionChannel Completion, FailureChannel Failure);

  private static Fixture _build(SteppedReadinessStrategy strategy, HeldLeaseCoordinator coordinator, TimeProvider clock) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var channel = new WorkChannel();
    var completion = new CompletionChannel();
    var failure = new FailureChannel();
    var worker = new OutboxPublishWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      workChannelWriter: channel,
      outboxCompletionChannel: completion,
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(new OutboxPublishWorkerOptions {
        Enabled = true,
        MaxOutboxAttempts = null,
        // No pacing delay: every pass waits on the strategy's readiness check, which the test releases.
        TransportNotReadyRetryDelayMilliseconds = 0,
      }),
      logger: NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      publishStrategy: strategy,
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = LEASE_SECONDS }),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      occurrenceGate: new NoOpOccurrencePublishGate(),
      leaseRegistry: new LeaseRegistry(),
      timeProvider: clock);
    return new Fixture(worker, channel, completion, failure);
  }

  private static OutboxWork _work() {
    var msgId = (Guid)TrackedGuid.New();
    return new OutboxWork {
      MessageId = msgId,
      Destination = "hold-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = ATTEMPTS,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  /// <summary>
  /// Each readiness check signals that it was entered and then waits for the test to release it with the
  /// answer to give, so a pass of the worker's loop runs only when the test says so.
  /// </summary>
  private sealed class SteppedReadinessStrategy(bool bulk) : IMessagePublishStrategy {
    private readonly System.Threading.Channels.Channel<bool> _entered = System.Threading.Channels.Channel.CreateUnbounded<bool>();
    private readonly System.Threading.Channels.Channel<bool> _answers = System.Threading.Channels.Channel.CreateUnbounded<bool>();
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public bool SupportsBulkPublish => bulk;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      _entered.Writer.TryWrite(true);
      return await _answers.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>Waits for the worker to enter its next readiness check.</summary>
    public async Task WaitForCheckAsync() => await _entered.Reader.ReadAsync().AsTask().WaitAsync(_signal);

    /// <summary>Waits for the next readiness check and answers it.</summary>
    public async Task PassAsync(bool ready) {
      await WaitForCheckAsync();
      Answer(ready);
    }

    /// <summary>Answers a readiness check already waited for with <see cref="WaitForCheckAsync"/>.</summary>
    public void Answer(bool ready) => _answers.Writer.TryWrite(ready);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      Published.Enqueue(work);
      return Task.FromResult(_success(work));
    }

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      foreach (var work in workItems) {
        Published.Enqueue(work);
      }
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>([.. workItems.Select(_success)]);
    }

    private static MessagePublishResult _success(OutboxWork work) =>
      new() { MessageId = work.MessageId, Success = true, CompletedStatus = work.Status };
  }

  private sealed class WorkChannel : IWorkChannelWriter {
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    public System.Threading.Channels.ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class CompletionChannel : IOutboxCompletionChannel {
    private readonly System.Threading.Channels.Channel<Guid> _completed = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      _completed.Writer.TryWrite(outboxMessageId);
      return ValueTask.CompletedTask;
    }

    public async Task WaitForCountAsync(int count) {
      for (var i = 0; i < count; i++) {
        await _completed.Reader.ReadAsync().AsTask().WaitAsync(_signal);
      }
    }
  }

  private sealed class FailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }
}
