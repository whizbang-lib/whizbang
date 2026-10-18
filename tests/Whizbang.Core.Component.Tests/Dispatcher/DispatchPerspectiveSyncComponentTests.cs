// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.ExecutorShapes.Owned;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A receptor marked <see cref="AwaitPerspectiveSyncAttribute"/> waits for its perspective once, before
/// it runs, whichever local path handles the message and whatever stage or shape the receptor has
/// (#1410 follow-up, E18). Real generated dispatcher; the awaiter records instead of waiting.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
public class DispatchPerspectiveSyncComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.ExecutorShapes.Owned";
  private static readonly string[] _waitThenHandle = ["waited", "handled"];
  private static readonly string[] _waitThenSyncedHandle = ["waited", "handled after Synced"];

  public static IEnumerable<Func<(string Entry, string Shape)>> Cases() {
    foreach (var entry in new[] { "PublishAsync", "PublishManyAsync", "LocalInvokeAsync", "CascadeMessageAsync" }) {
      foreach (var shape in new[] { "default", "inline", "sync" }) {
        yield return () => (entry, shape);
      }
    }
  }

  [Test]
  [MethodDataSource(nameof(Cases))]
  public async Task AnAwaitingReceptor_WaitsOnceBeforeItRunsAsync(string entry, string shape) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    IMessage message = shape switch {
      "default" => new ExAwaitedEvent(id),
      "inline" => new ExAwaitedInlineEvent(id),
      _ => new ExAwaitedSyncEvent(id),
    };

    switch (entry) {
      case "PublishAsync": await dispatcher.PublishAsync((object)message); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { message }); break;
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)message); break;
      default: await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Both); break;
    }

    await Assert.That(ExSyncLog.For(id)).IsEquivalentTo(_waitThenHandle, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because($"{entry} with a {shape} receptor waits for its perspective exactly once, and before the receptor runs");
  }

  [Test]
  [Arguments("PublishAsync")]
  [Arguments("LocalInvokeAsync")]
  [Arguments("CascadeMessageAsync")]
  public async Task AFireAlwaysReceptor_SeesTheOutcomeOfItsWaitAsync(string entry) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var message = new ExFireAlwaysEvent(id);

    switch (entry) {
      case "PublishAsync": await dispatcher.PublishAsync(message); break;
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)message); break;
      default: await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Both); break;
    }

    await Assert.That(ExSyncLog.For(id)).IsEquivalentTo(_waitThenSyncedHandle, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a receptor that fires whatever the outcome reads that outcome from its sync context");
  }

  [Test]
  [Arguments("PublishAsync")]
  [Arguments("LocalInvokeAsync")]
  [Arguments("CascadeMessageAsync")]
  public async Task AWaitThatTimesOut_RefusesAFireOnSuccessReceptorAndUsesTheDefaultTimeoutAsync(string entry) {
    var awaiter = new TimingOutSyncAwaiter();
    await using var provider = _buildProvider(awaiter);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var message = new ExAwaitedEvent(id);

    Task dispatch() => entry switch {
      "PublishAsync" => dispatcher.PublishAsync(message),
      "LocalInvokeAsync" => dispatcher.LocalInvokeAsync((object)message).AsTask(),
      _ => dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Both),
    };

    await Assert.That(dispatch).Throws<PerspectiveSyncTimeoutException>()
      .Because("FireOnSuccess, the default, refuses to run the receptor on stale data");
    await Assert.That(ExSyncLog.For(id)).DoesNotContain("handled");
    await Assert.That(awaiter.Timeouts).Contains(TimeSpan.FromMilliseconds(AwaitPerspectiveSyncAttribute.DefaultTimeoutMs))
      .Because("an attribute that names no timeout waits for the default");
  }

  private static ServiceProvider _buildProvider(IPerspectiveSyncAwaiter? awaiter = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => new PolicyRecordingOutbox());
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    if (awaiter is null) {
      services.AddScoped<IPerspectiveSyncAwaiter, RecordingSyncAwaiter>();
    } else {
      services.AddSingleton(awaiter);
    }
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  private sealed class TimingOutSyncAwaiter : IPerspectiveSyncAwaiter {
    private readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> _timeouts = new();

    public Guid AwaiterId { get; } = Guid.CreateVersion7();

    public IReadOnlyList<TimeSpan> Timeouts => [.. _timeouts];

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("these receptors wait on their stream");

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(false);

    public Task<SyncResult> WaitForStreamAsync(Type perspectiveType, Guid streamId, Type[]? eventTypes, TimeSpan timeout,
        Guid? eventIdToAwait = null, CancellationToken ct = default) {
      _timeouts.Enqueue(timeout);
      return Task.FromResult(new SyncResult(SyncOutcome.TimedOut, 0, timeout));
    }
  }

  private sealed class RecordingSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.CreateVersion7();

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("these receptors wait on their stream");

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task<SyncResult> WaitForStreamAsync(Type perspectiveType, Guid streamId, Type[]? eventTypes, TimeSpan timeout,
        Guid? eventIdToAwait = null, CancellationToken ct = default) {
      ExSyncLog.Add("waited", streamId);
      return Task.FromResult(new SyncResult(SyncOutcome.Synced, 0, TimeSpan.Zero));
    }
  }
}
