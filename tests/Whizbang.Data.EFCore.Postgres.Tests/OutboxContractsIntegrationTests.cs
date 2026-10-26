// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.OutboxContracts;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests.OutboxContracts {
  /// <summary>An event that records when it was written to the outbox (#1557).</summary>
  public sealed record OcQueuedAtEvent(
      [property: StreamId] Guid Id,
      [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null) : IEvent;
}

namespace Whizbang.Data.EFCore.Postgres.Tests {
  /// <summary>
  /// Two outbox contracts through the REAL store and the REAL drain worker: a message's QueuedAt is stamped when
  /// the dispatcher writes it to the outbox and travels in the stored copy to the wire (#1557), and while the
  /// transport reports not ready the drain worker holds the row, renewing its lease in the database without
  /// charging an attempt, and publishes it once the transport is ready (#1558). Only the transport and the
  /// worker's channels are faked (<see cref="OutboxDrainHarness"/>); every wait is on a worker signal.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
  /// <docs>extending/extensibility/custom-health-checks#whizbang-extension-point-itransportreadinesscheck</docs>
  [Category("Integration")]
  [NotInParallel("EFCorePostgresTests")]
  [Category("Shard4")]
  public class OutboxContractsIntegrationTests : EFCoreTestBase {
    private const string TENANT = "tenant-outbox-contracts";
    private static readonly DateTimeOffset _queuedAt = new(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Test]
    public async Task QueuedAt_StampedOnTheOutboxWrite_ReachesTheWireAsync() {
      var (provider, jsonOptions) = _createService();
      await using var _ = provider;
      var id = Guid.CreateVersion7();

      await _publishAsync(provider, id);
      var row = await _theOnlyRowAsync();
      await OutboxDrainHarness.LeaseAsync(ConnectionString, provider.GetRequiredService<IServiceInstanceProvider>(), row.MessageId);
      var published = await OutboxDrainHarness.DrainAndCaptureAsync(provider, jsonOptions, bulkPublish: false, row.MessageId, row.StreamId ?? Guid.Empty);

      await Assert.That(published).Count().IsEqualTo(1);
      await Assert.That(_queuedAtOf(published[0].Envelope.Payload)).IsEqualTo(_queuedAt)
        .Because("the stored copy carries the time it was written to the outbox, and the drain sends that copy");
    }

    [Test]
    public async Task TransportNotReady_HoldsTheRowWithItsLeaseRenewed_ChargesNoAttempt_ThenPublishesAsync() {
      var (provider, jsonOptions) = _createService();
      await using var _ = provider;
      var id = Guid.CreateVersion7();

      await _publishAsync(provider, id);
      var row = await _theOnlyRowAsync();
      await OutboxDrainHarness.LeaseAsync(ConnectionString, provider.GetRequiredService<IServiceInstanceProvider>(), row.MessageId);
      var leased = await _readRowAsync(row.MessageId);

      var (held, published) = await OutboxDrainHarness.HoldThenDrainAsync(
        provider, jsonOptions, row.MessageId, row.StreamId ?? Guid.Empty, renewalLeaseSeconds: 3600, whileHeld: async () => {
          var whileHeld = await _readRowAsync(row.MessageId);
          await Assert.That(whileHeld.Attempts).IsEqualTo(leased.Attempts)
            .Because("holding a row for a transport that is not ready charges no attempt");
          await Assert.That(whileHeld.LeaseExpiry!.Value).IsGreaterThan(leased.LeaseExpiry!.Value.AddMinutes(30))
            .Because("the held row's lease was renewed, by the hour the renewal options give, so it is not re-claimed and charged");
          await Assert.That(whileHeld.ProcessedAt).IsNull().Because("nothing was published");
        });

      await Assert.That(held.HeldMessageIds).IsEquivalentTo([row.MessageId]);
      await Assert.That(published.Select(w => w.MessageId)).IsEquivalentTo([row.MessageId])
        .Because("once the transport is ready the held row publishes");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static DateTimeOffset? _queuedAtOf(JsonElement payload) {
      foreach (var property in payload.EnumerateObject()) {
        if (string.Equals(property.Name, nameof(OcQueuedAtEvent.QueuedAt), StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind == JsonValueKind.String) {
          return property.Value.GetDateTimeOffset();
        }
      }
      return null;
    }

    private static Task _publishAsync(ServiceProvider provider, Guid id) {
      var dispatcher = provider.GetRequiredService<IDispatcher>();
      return dispatcher.AsSystem().ForTenant(TENANT).RunAsync(async _ => {
        await dispatcher.PublishAsync(new OcQueuedAtEvent(id));
        return true;
      });
    }

    private async Task<OutboxRecord> _theOnlyRowAsync() {
      await using var dbContext = CreateDbContext();
      var messageType = TypeNameFormatter.AssemblyQualifiedName(typeof(OcQueuedAtEvent));
      var rows = await dbContext.Outbox.AsNoTracking().Where(m => m.MessageType == messageType).ToListAsync();
      await Assert.That(rows).Count().IsEqualTo(1).Because("the publish writes exactly one copy");
      return rows[0];
    }

    private async Task<OutboxRecord> _readRowAsync(Guid messageId) {
      await using var dbContext = CreateDbContext();
      return await dbContext.Outbox.AsNoTracking().SingleAsync(m => m.MessageId == messageId);
    }

    private (ServiceProvider Provider, JsonSerializerOptions JsonOptions) _createService() {
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "oc-sender", "oc-sender-host", 1));
      services.AddScoped(_ => CreateDbContext());
      services.Configure<RoutingOptions>(options => options.OwnDomains(typeof(OcQueuedAtEvent).Namespace!));

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
      // The dispatcher's clock: the stamp must come from it, not from the wall clock.
      services.AddSingleton<TimeProvider>(new FakeTimeProvider(_queuedAt));

      return (services.BuildServiceProvider(), jsonOptions);
    }
  }
}
