// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1385: a framework system command sent with <c>IDispatcher.SendAsync</c> is
/// broadcast through the REAL outbox in a real PostgreSQL database, and the sending service runs its
/// handler exactly once. The handler is registered the way the framework's registrars register theirs,
/// one instance at <see cref="LifecycleStage.LocalImmediateInline"/> and at
/// <see cref="LifecycleStage.PreOutboxInline"/>. The send runs it in-process; the outbox row then
/// reaches the REAL <see cref="OutboxDrainWorker"/>, whose PreOutbox stage must recognize the
/// locally-dispatched copy and not run it again. What this proves that a component test cannot: the
/// local-dispatch flag survives serialization into <c>wh_outbox</c> and back.
/// </summary>
/// <remarks>
/// Only the transport is faked: the publish strategy reports success and records what it published,
/// which is the broadcast. The row the send wrote is leased to this instance the way the claim leaves
/// it for the drainer. Every assertion waits on the row's completion signal, never on time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class SystemCommandBroadcastIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.SomeOwnedDomain";

  [Test]
  [Arguments(false, false)]
  [Arguments(false, true)]
  [Arguments(true, false)]
  [Arguments(true, true)]
  public async Task SendAsync_SystemCommandHandledLocally_IsBroadcastAndRunsOnceOnTheSenderAsync(bool withOwnedDomain, bool bulkPublish) {
    var probe = new CountingReceptor<ClearCacheCommand>();
    var (serviceProvider, jsonOptions) = _createServices(withOwnedDomain);
    var registry = serviceProvider.GetRequiredService<IReceptorRegistry>();
    registry.Register<ClearCacheCommand>(probe, LifecycleStage.LocalImmediateInline);
    registry.Register<ClearCacheCommand>(probe, LifecycleStage.PreOutboxInline);

    var cacheKey = "cache-" + Guid.CreateVersion7();
    // A security context on the envelope: the drain worker runs no stage for a row without one
    await serviceProvider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-system-broadcast")
      .SendAsync(new ClearCacheCommand(CacheKey: cacheKey));

    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("the sending service handles its own copy in-process before SendAsync returns");

    var published = await _drainTheSentRowAsync(serviceProvider, jsonOptions, bulkPublish);

    await Assert.That(published.Count).IsEqualTo(1)
      .Because("a system command is broadcast even when the sender handled it");
    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("the outbox copy is flagged as locally dispatched, so PreOutbox must not run the handler again");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendAsync_SystemCommandWithOnlyAPreOutboxHandler_RunsItAtPreOutboxOnceAsync(bool bulkPublish) {
    var probe = new CountingReceptor<ClearCacheCommand>();
    var (serviceProvider, jsonOptions) = _createServices(withOwnedDomain: false);
    serviceProvider.GetRequiredService<IReceptorRegistry>().Register<ClearCacheCommand>(probe, LifecycleStage.PreOutboxInline);

    var cacheKey = "cache-" + Guid.CreateVersion7();
    // A security context on the envelope: the drain worker runs no stage for a row without one
    await serviceProvider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-system-broadcast")
      .SendAsync(new ClearCacheCommand(CacheKey: cacheKey));

    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(0)
      .Because("nothing handles the command locally, so the send only queues it");

    var published = await _drainTheSentRowAsync(serviceProvider, jsonOptions, bulkPublish);

    await Assert.That(published.Count).IsEqualTo(1);
    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("an outbox copy nothing dispatched locally is where the sender's own handler runs");
  }

  /// <summary>
  /// Leases the one row the send wrote to this instance, runs the real drain worker over it, and
  /// returns the ids the fake transport published.
  /// </summary>
  private async Task<IReadOnlyCollection<Guid>> _drainTheSentRowAsync(
      ServiceProvider serviceProvider, System.Text.Json.JsonSerializerOptions jsonOptions, bool bulkPublish) {
    try {
      await using var dbContext = CreateDbContext();
      var commandType = TypeNameFormatter.AssemblyQualifiedName(typeof(ClearCacheCommand));
      var row = await dbContext.Outbox.AsNoTracking().SingleAsync(m => m.MessageType == commandType);
      await OutboxDrainHarness.LeaseAsync(ConnectionString, serviceProvider.GetRequiredService<IServiceInstanceProvider>(), row.MessageId);
      return await OutboxDrainHarness.DrainAsync(serviceProvider, jsonOptions, bulkPublish, row.MessageId, row.StreamId ?? Guid.Empty);
    } finally {
      await serviceProvider.DisposeAsync();
    }
  }

  private (ServiceProvider Provider, System.Text.Json.JsonSerializerOptions JsonOptions) _createServices(bool withOwnedDomain) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());
    if (withOwnedDomain) {
      services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    }

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter(), detachedWork: new DetachedLifecycleWork()));

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  /// <summary>Counts invocations per cache key. Generic so the source generator does not discover it.</summary>
  private sealed class CountingReceptor<TCommand> : IReceptor<TCommand> where TCommand : ClearCacheCommand {
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public int CountFor(string cacheKey) => _counts.TryGetValue(cacheKey, out var n) ? n : 0;

    public ValueTask HandleAsync(TCommand message, CancellationToken cancellationToken = default) {
      _counts.AddOrUpdate(message.CacheKey!, 1, static (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }
}
