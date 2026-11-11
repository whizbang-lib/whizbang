// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the slice 12 (Half B) DI contract for <see cref="IOutboxBatchStrategy"/>:
/// <list type="bullet">
///   <item>Default registration via <see cref="WorkerPipelineExtensions.AddWhizbangWorkers"/>
///   resolves to <see cref="SlidingWindowOutboxBatchStrategy"/> with the 50 ms / 1 s / 100
///   defaults.</item>
///   <item><see cref="WorkerPipelineExtensions.AddWhizbangOutboxStrategy{TStrategy}"/> swaps
///   the registered implementation. Used by low-throughput tenants who opt to
///   <see cref="ImmediateOutboxBatchStrategy"/> for back-compat / strict-ordering semantics.</item>
///   <item><see cref="SlidingWindowOutboxOptions"/> defaults remain 50 ms / 1 s / 100.</item>
/// </list>
/// </summary>
public class OutboxBatchStrategyRegistrationTests {

  [Test]
  public async Task AddWhizbang_DefaultRegistration_ResolvesCoalescingOutboxAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOutboxBatchStrategy>();

    // #2220: every registered strategy is wrapped in the batch buffer's bound.
    await Assert.That(resolved).IsTypeOf<BoundedOutboxBatchStrategy>();
    // #2220 Part 2: the default coalesces writes from many streams into one flush.
    await Assert.That(((BoundedOutboxBatchStrategy)resolved).Inner).IsTypeOf<CoalescingOutboxBatchStrategy>();
  }

  [Test]
  public async Task AddWhizbangOutboxStrategy_ImmediateOverride_ResolvesImmediateAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOutboxBatchStrategy>();

    await Assert.That(resolved).IsTypeOf<BoundedOutboxBatchStrategy>();
    await Assert.That(((BoundedOutboxBatchStrategy)resolved).Inner).IsTypeOf<ImmediateOutboxBatchStrategy>();
  }

  [Test]
  public async Task ThePerStreamSlidingWindow_CanStillBeChosen_AndTheCoalescingSectionBindsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:CoalescingOutbox:Partitions"] = "3",
      ["Whizbang:Workers:CoalescingOutbox:MaxSize"] = "250",
    }).Build());
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<SlidingWindowOutboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();

    await Assert.That(((BoundedOutboxBatchStrategy)sp.GetRequiredService<IOutboxBatchStrategy>()).Inner).IsTypeOf<SlidingWindowOutboxBatchStrategy>();
    await Assert.That(sp.GetRequiredService<CoalescingOutboxBatchStrategy>().PartitionCount).IsEqualTo(3);
    await Assert.That(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CoalescingOutboxOptions>>().Value.MaxSize).IsEqualTo(250);
  }

  [Test]
  public async Task ACustomStrategy_IsWrappedTooAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddSingleton<CustomOutboxStrategy>();
    services.AddWhizbangOutboxStrategy<CustomOutboxStrategy>();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOutboxBatchStrategy>();

    await Assert.That(((BoundedOutboxBatchStrategy)resolved).Inner).IsTypeOf<CustomOutboxStrategy>();
  }

  [Test]
  public async Task TheBatchBufferSection_BindsTheLimits_AndTheLimitsAreReportedAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:BatchBuffer:MaxBufferedMessages"] = "7",
        ["Whizbang:BatchBuffer:MaxBufferedBytes"] = "4096",
        ["Whizbang:BatchBuffer:TransactionCeilingMultiplier"] = "3",
      }).Build());
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var limits = sp.GetRequiredService<BatchBufferLimits>();

    await Assert.That(limits.MaxMessages).IsEqualTo(7);
    await Assert.That(limits.MaxBytes).IsEqualTo(4096);
    await Assert.That(limits.TransactionCeilingMultiplier).IsEqualTo(3.0);
    await Assert.That(sp.GetServices<Whizbang.Core.Configuration.IDerivedSettingSource>()).Contains(limits);
  }

  [Test]
  public async Task WithNoConfiguration_TheLimitsAreTheDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var limits = sp.GetRequiredService<BatchBufferLimits>();

    await Assert.That(limits.MaxMessages).IsEqualTo(BatchBufferOptions.DEFAULT_MAX_BUFFERED_MESSAGES);
    await Assert.That(limits.MaxBytes).IsEqualTo((long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * BatchBufferOptions.DEFAULT_MAX_BUFFERED_BYTES_FRACTION));
  }

  [Test]
  public async Task AStoredBatch_GivesItsRoomBack_ThroughTheRegisteredStoreCallbacksAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IWorkCoordinator>(_ => new NoOpWorkCoordinator());
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();
    services.AddWhizbangInboxStrategy<ImmediateInboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    await sp.GetRequiredService<IOutboxBatchStrategy>().AppendAsync(BoundedBatchStrategyTests.Outbox());
    await sp.GetRequiredService<IInboxBatchStrategy>().AppendAsync(BoundedBatchStrategyTests.Inbox());

    await Assert.That(sp.GetRequiredService<BatchBuffer>().BufferedMessages).IsEqualTo(0)
      .Because("the immediate strategies stored each message through the registered callback, which gave its room back");
  }

  private sealed class CustomOutboxStrategy : IOutboxBatchStrategy {
    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  [Test]
  public async Task SlidingWindowOutboxOptions_DefaultsLockedAt50ms1s100Async() {
    var defaults = new SlidingWindowOutboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(50));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(defaults.MaxSize).IsEqualTo(100);
  }
}
