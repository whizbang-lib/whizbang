// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// #1875: a test host built from the application's entry point (<c>WebApplicationFactory&lt;Program&gt;</c>) is stopped
/// twice at once. Disposing the factory stops the host and then disposes it; the application's own <c>Run</c> wakes on
/// <c>ApplicationStopping</c>, stops the same host a second time, and disposes it as soon as that second stop returns.
/// When the second stop finished first, the first one reached the batchers' stop-time flush with the container already
/// disposed, and the flush failed with <see cref="ObjectDisposedException"/> resolving the batchers.
/// </summary>
/// <remarks>
/// The test plays both callers against a host with the batchers' stop-time flush and a buffering outbox batcher. A
/// hosted service holds the factory's stop (and only that one) inside <see cref="IHostedService.StopAsync"/>, so the
/// application's stop runs to the end and disposes the host first: the ordering the race produces.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ConcurrentHostStopTests {

  /// <summary>Holds the stop made by the flow that called <see cref="HoldThisFlow"/> until released; any other stop passes.</summary>
  private sealed class HeldStop : IHostedService {
    private static readonly AsyncLocal<bool> _holdThisFlow = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void HoldThisFlow() => _holdThisFlow.Value = true;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _release.TrySetResult();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) {
      if (_holdThisFlow.Value) {
        Entered.TrySetResult();
        await _release.Task.ConfigureAwait(false);
      }
    }
  }

  [Test]
  public async Task AHostStoppedTwiceAtOnce_AndDisposedByTheStopThatFinishesFirst_StopsCleanly_AndFlushesTheBufferedBatchAsync() {
    var held = new HeldStop();
    var stored = new ConcurrentQueue<(Guid MessageId, bool ProviderAlive)>();
    IServiceProvider? root = null;
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.TryAddWhizbangDefaults();
    builder.Services.AddSingleton<ProviderLiveness>();
    builder.Services.AddHostedService(_ => held);
    // Buffers until stopped: the fake clock never moves, so only the stop-time flush stores the message
    builder.Services.AddSingleton<IOutboxBatchStrategy>(new SlidingWindowOutboxBatchStrategy(
      (messages, _) => {
        foreach (var message in messages) {
          stored.Enqueue((message.MessageId, ProviderLiveness.IsAlive(root!)));
        }
        return Task.CompletedTask;
      },
      NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      new SlidingWindowOutboxOptions { MaxSize = 100 },
      new FakeTimeProvider()));
    var host = builder.Build();
    root = host.Services;
    await host.StartAsync();
    var buffered = HostShutdown.OutboxMessage();
    await host.Services.GetRequiredService<IOutboxBatchStrategy>().AppendAsync(buffered);

    // The application's Run: stop once ApplicationStopping fires, then dispose the host
    var application = Task.Run(async () => {
      try {
        await host.WaitForShutdownAsync().ConfigureAwait(false);
      } finally {
        await ((IAsyncDisposable)host).DisposeAsync().ConfigureAwait(false);
      }
    });
    // The factory's dispose: stop the host, then dispose it
    var factory = Task.Run(async () => {
      HeldStop.HoldThisFlow();
      await host.StopAsync().ConfigureAwait(false);
      host.Dispose();
    });
    await held.Entered.Task;
    // The application's stop and dispose finish while the factory's stop is still in progress
    await application;
    held.Release();

    await Assert.That(async () => await factory).ThrowsNothing()
      .Because("a stop that finishes after the other stop disposed the host must not touch the disposed provider");
    await Assert.That(stored.Select(s => s.MessageId)).IsEquivalentTo([buffered.MessageId])
      .Because("the buffered batch is flushed once, by whichever stop reaches the flush first");
    await Assert.That(stored.All(s => s.ProviderAlive)).IsTrue()
      .Because("the flush stores while the host's service provider is still alive");
  }
}
