// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Owned;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Upstream;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A dispatched message's whole trip (#1410 follow-up): the sender's REAL outbox, the REAL drain worker
/// building the wire envelope, and the REAL inbox worker of whichever service receives it, the sender
/// itself or another. Every message is handled exactly once per service that handles it: a copy the
/// sender already handled is not run again when it comes back, a scheduled copy whose run was deferred
/// runs when it comes back, and another service runs what it receives.
/// </summary>
/// <remarks>
/// Only the transport edge and the workers' channels are faked (<see cref="OutboxDrainHarness"/>,
/// <see cref="InboxDeliveryHarness"/>). The wire envelope is serialized and read back between the two
/// services. A run waits on the receptor's own signal; a skip waits on the receptor invoker reporting the
/// skip for that message. Nothing waits on time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class DispatchRoundTripIntegrationTests : EFCoreTestBase {
  private const string SENDER = "dx-sender";
  private const string RECEIVER = "dx-receiver";
  private const string SENDER_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Owned";
  private const string RECEIVER_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Upstream";
  private const string TENANT = "tenant-dispatch-round-trip";
  private static readonly DateTimeOffset _scheduledAt = new(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);

  [Test]
  [Arguments("PublishAsync", false)]
  [Arguments("PublishManyAsync", true)]
  [Arguments("SendAsync", false)]
  [Arguments("SendManyAsync", true)]
  public async Task ScheduledDispatch_RunsNothingNow_ThenRunsOnceWhenItsCopyComesBackAsync(string entry, bool bulkPublish) {
    var sender = _createService(SENDER, SENDER_DOMAIN);
    await using var _ = sender.Provider;
    var id = Guid.CreateVersion7();

    await _dispatchAsync(sender.Provider, entry, id, new DispatchOptions().WithScheduledFor(_scheduledAt));
    await Assert.That(RoundTripProbe.Count(SENDER, id)).IsEqualTo(0).Because($"{entry}: a scheduled dispatch runs nothing now");

    var published = await _drainTheOnlyCopyAsync(sender, entry, bulkPublish);
    await Assert.That(published.Envelope.DispatchContext.LocalDeferred).IsTrue()
      .Because($"{entry}: the wire envelope says the sender handles it when it comes back");
    await InboxDeliveryHarness.DeliverAsync(sender.Provider, sender.JsonOptions, published, RoundTripProbe.Ran(SENDER, id).Task);

    await Assert.That(RoundTripProbe.Count(SENDER, id)).IsEqualTo(1)
      .Because($"{entry}: the deferred run happens when the copy comes back, exactly once");
  }

  [Test]
  [Arguments("PublishAsync", false)]
  [Arguments("PublishManyAsync", true)]
  [Arguments("SendAsync", false)]
  [Arguments("SendManyAsync", true)]
  [Arguments("CascadeBoth", false)]
  public async Task HandledCopy_ComesBackToItsSender_AndIsNotRunAgainAsync(string entry, bool bulkPublish) {
    var skipped = new SkippedPostInboxSignal();
    var sender = _createService(SENDER, SENDER_DOMAIN, skipped);
    await using var _ = sender.Provider;
    var id = Guid.CreateVersion7();

    await _dispatchAsync(sender.Provider, entry, id, options: null);
    await Assert.That(RoundTripProbe.Count(SENDER, id)).IsEqualTo(1).Because($"{entry}: the sender handles it before the call returns");

    var published = await _drainTheOnlyCopyAsync(sender, entry, bulkPublish);
    await Assert.That(published.Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue()
      .Because($"{entry}: the wire envelope says the sender handled it");
    await InboxDeliveryHarness.DeliverAsync(sender.Provider, sender.JsonOptions, published, skipped.For(published.MessageId));

    await Assert.That(RoundTripProbe.Count(SENDER, id)).IsEqualTo(1)
      .Because($"{entry}: a copy the sender already handled is not run again when it comes back");
  }

  [Test]
  [Arguments("PublishAsync", false)]
  [Arguments("PublishManyAsync", true)]
  [Arguments("SendAsync", false)]
  [Arguments("SendManyAsync", true)]
  [Arguments("CascadeBoth", false)]
  public async Task HandledCopy_ReachesAnotherService_AndRunsOnceThereAsync(string entry, bool bulkPublish) {
    var sender = _createService(SENDER, SENDER_DOMAIN);
    var (receiverProvider, receiverJsonOptions) = _createService(RECEIVER, RECEIVER_DOMAIN);
    await using var _ = sender.Provider;
    await using var __ = receiverProvider;
    var id = Guid.CreateVersion7();

    await _dispatchAsync(sender.Provider, entry, id, options: null);
    var published = await _drainTheOnlyCopyAsync(sender, entry, bulkPublish);
    await InboxDeliveryHarness.DeliverAsync(receiverProvider, receiverJsonOptions, published, RoundTripProbe.Ran(RECEIVER, id).Task);

    await Assert.That(RoundTripProbe.Count(SENDER, id)).IsEqualTo(1).Because($"{entry}: the sender handled it once");
    await Assert.That(RoundTripProbe.Count(RECEIVER, id)).IsEqualTo(1)
      .Because($"{entry}: the copy says only that the sender handled it; the receiving service runs it once");
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private static bool _isSend(string entry) => entry.StartsWith("Send", StringComparison.Ordinal);

  /// <summary>
  /// Dispatches inside the system tenant's context, so the envelope carries one: neither worker runs a
  /// stage for a message without it.
  /// </summary>
  private static Task _dispatchAsync(ServiceProvider provider, string entry, Guid id, DispatchOptions? options) {
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    object message = _isSend(entry) ? new DxRoundTripCommand(id) : new DxRoundTripEvent(id);
    options ??= new DispatchOptions();
    return dispatcher.AsSystem().ForTenant(TENANT).RunAsync(async ct => {
      switch (entry) {
        case "PublishAsync": await dispatcher.PublishAsync(message, options); break;
        case "PublishManyAsync": await dispatcher.PublishManyAsync([message], options); break;
        case "SendAsync": await dispatcher.SendAsync(message, options); break;
        case "SendManyAsync": await dispatcher.SendManyAsync([message], options); break;
        default: await dispatcher.CascadeMessageAsync((IMessage)message, sourceEnvelope: null, DispatchModes.Both, ct); break;
      }
      return true;
    }, options.CancellationToken);
  }

  private async Task<OutboxWork> _drainTheOnlyCopyAsync((ServiceProvider Provider, JsonSerializerOptions JsonOptions) sender, string entry, bool bulkPublish) {
    await using var dbContext = CreateDbContext();
    var messageType = TypeNameFormatter.AssemblyQualifiedName(_isSend(entry) ? typeof(DxRoundTripCommand) : typeof(DxRoundTripEvent));
    var rows = await dbContext.Outbox.AsNoTracking().Where(m => m.MessageType == messageType).ToListAsync();
    await Assert.That(rows).Count().IsEqualTo(1).Because($"{entry}: the dispatch writes exactly one copy");
    var row = rows[0];
    if (row.ScheduledFor is not null) {
      await Assert.That(row.ScheduledFor).IsEqualTo(_scheduledAt).Because($"{entry}: the copy waits for its schedule");
      await OutboxDrainHarness.MakeDueAsync(ConnectionString, row.MessageId);
    }
    await OutboxDrainHarness.LeaseAsync(ConnectionString, sender.Provider.GetRequiredService<IServiceInstanceProvider>(), row.MessageId);
    var published = await OutboxDrainHarness.DrainAndCaptureAsync(sender.Provider, sender.JsonOptions, bulkPublish, row.MessageId, row.StreamId ?? Guid.Empty);
    await Assert.That(published).Count().IsEqualTo(1).Because($"{entry}: the drain worker publishes the copy once");
    return published[0];
  }

  private (ServiceProvider Provider, JsonSerializerOptions JsonOptions) _createService(string serviceName, string ownedDomain, ILoggerProvider? logSignal = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), serviceName, serviceName + "-host", 1));
    services.AddScoped(_ => CreateDbContext());
    services.Configure<RoutingOptions>(options => options.OwnDomains(ownedDomain));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => {
      if (logSignal is null) {
        builder.SetMinimumLevel(LogLevel.Warning);
        return;
      }
      // The skip this test waits on is reported at Debug
      builder.SetMinimumLevel(LogLevel.Debug);
      builder.AddProvider(logSignal);
    });

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
}
