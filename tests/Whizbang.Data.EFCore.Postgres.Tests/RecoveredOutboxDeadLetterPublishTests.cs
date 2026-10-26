// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
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
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.RecoveredOutbox;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests.RecoveredOutbox {
  /// <summary>An event this service publishes, dead-lettered on the way out and then recovered (#1722).</summary>
  public sealed record RecoveredOutboxEvent([property: StreamId] Guid Id, string Reference) : IEvent;
}

namespace Whizbang.Data.EFCore.Postgres.Tests {
  /// <summary>
  /// An outbox message that was dead-lettered and then recovered is published as the message it was stored as, and
  /// its dead letter reads Recovered only once it has been published (#1722).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Recovery re-inserted the outbox row with a column list of its own: the envelope type <c>recovered</c>, not an
  /// event, background priority, and the dead letter marked Recovered in the same call, before anything was published.
  /// The drain hands the row's envelope type to the transport as the wire's envelope type header, which a receiving
  /// service resolves the envelope by, so a recovered message reached the broker under a name no receiver knows.
  /// </para>
  /// <para>
  /// Real PostgreSQL and the real pieces throughout: the dispatcher writes the row, the dead-letter function moves it,
  /// one scan of the recovery worker over the Postgres recovery service re-drives it, and the real drain worker
  /// publishes it (<see cref="OutboxDrainHarness"/>; only the transport is faked, and it records what it was handed).
  /// </para>
  /// </remarks>
  /// <docs>operations/dead-letter-queue/recovery</docs>
  /// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
  [Category("Integration")]
  [NotInParallel("EFCorePostgresTests")]
  [Category("Shard3")]
  public class RecoveredOutboxDeadLetterPublishTests : EFCoreTestBase {
    private const string TENANT = "tenant-recovered-outbox";

    [Test]
    public async Task ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync() {
      var (provider, jsonOptions) = _createService();
      await using var _ = provider;
      var orderId = (Guid)TrackedGuid.New();

      await _publishAsync(provider, orderId);
      var stored = await _storedRowAsync(orderId);
      var id = stored.MessageId;
      var deadLetterId = await _deadLetterAsync(id);
      await _recoverAsync(provider, deadLetterId);
      var afterRecovery = await _deadLetterStatusAsync(deadLetterId);

      await OutboxDrainHarness.LeaseAsync(ConnectionString, provider.GetRequiredService<IServiceInstanceProvider>(), id);
      var published = await OutboxDrainHarness.DrainAndCaptureAsync(provider, jsonOptions, bulkPublish: false, id, stored.StreamId);

      await Assert.That(published).Count().IsEqualTo(1);
      var work = published[0];
      await Assert.That(work.EnvelopeType).IsEqualTo(stored.EnvelopeType)
        .Because("the drain sends the row's envelope type as the wire's envelope type, and a receiver resolves the envelope by it");
      await Assert.That(JsonContextRegistry.GetTypeInfoByName(work.EnvelopeType, jsonOptions)).IsNotNull()
        .Because("a receiving service can read the recovered message");
      await Assert.That(work.IsEvent).IsTrue()
        .Because("the recovered message is the event it was stored as, so it is routed as one");
      await Assert.That(work.Priority).IsEqualTo(stored.Priority)
        .Because("recovery re-enters the message with the priority it was stored with");
      await Assert.That(work.Envelope.MessageId.Value).IsEqualTo(id);
      await Assert.That(afterRecovery).IsEqualTo((int)DeadLetterRecoveryStatus.Recovering)
        .Because("the dead letter reads Recovered only once the message has been published, not when it re-enters");

      // The completion flush the drain's completion feeds.
      await using (var scope = provider.CreateAsyncScope()) {
        await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>().CompleteOutboxPublishedAsync([id]);
      }

      await Assert.That(await _deadLetterStatusAsync(deadLetterId)).IsEqualTo((int)DeadLetterRecoveryStatus.Recovered)
        .Because("the recovered message was published");
    }

    [Test]
    public async Task AnOutboxDeadLetterCapturedBeforeItKeptTheEnvelopeType_IsPublishedUnderTheNameItsStoreWritesAsync() {
      var (provider, jsonOptions) = _createService();
      await using var _ = provider;
      var orderId = (Guid)TrackedGuid.New();

      await _publishAsync(provider, orderId);
      var stored = await _storedRowAsync(orderId);
      var id = stored.MessageId;
      var deadLetterId = await _deadLetterAsync(id);
      // A dead letter captured before the capture kept every field the store needs held the body and the metadata only.
      await using (var connection = await _openAsync()) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
          UPDATE wh_dead_letters
          SET envelope = jsonb_build_object('event_data', envelope -> 'event_data', 'metadata', envelope -> 'metadata')
          WHERE dead_letter_id = @id
          """;
        command.Parameters.AddWithValue("id", deadLetterId);
        await command.ExecuteNonQueryAsync();
      }
      await _recoverAsync(provider, deadLetterId);

      await OutboxDrainHarness.LeaseAsync(ConnectionString, provider.GetRequiredService<IServiceInstanceProvider>(), id);
      var published = await OutboxDrainHarness.DrainAndCaptureAsync(provider, jsonOptions, bulkPublish: false, id, stored.StreamId);

      await Assert.That(published).Count().IsEqualTo(1);
      await Assert.That(published[0].EnvelopeType).IsEqualTo(stored.EnvelopeType)
        .Because("the store writes the envelope type its message type names, and that is the name a receiver resolves");
      await Assert.That(published[0].IsEvent).IsTrue()
        .Because("the event store holds the message, so it was stored as an event");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static Task _publishAsync(ServiceProvider provider, Guid id) {
      var dispatcher = provider.GetRequiredService<IDispatcher>();
      return dispatcher.AsSystem().ForTenant(TENANT).RunAsync(async _ => {
        await dispatcher.PublishAsync(new RecoveredOutboxEvent(id, "R-1"));
        return true;
      });
    }

    private sealed record StoredRow(Guid MessageId, Guid StreamId, string EnvelopeType, int Priority);

    /// <summary>The one outbox row the publish wrote for the event; the event's id is its stream.</summary>
    private async Task<StoredRow> _storedRowAsync(Guid streamId) {
      await using var connection = await _openAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = "SELECT message_id, stream_id, envelope_type, priority, is_event FROM wh_outbox WHERE stream_id = @id";
      command.Parameters.AddWithValue("id", streamId);
      await using var reader = await command.ExecuteReaderAsync();
      await Assert.That(await reader.ReadAsync()).IsTrue().Because("the publish writes the outbox row");
      await Assert.That(reader.GetBoolean(4)).IsTrue().Because("the dispatcher stores the event as an event");
      return new StoredRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3));
    }

    /// <summary>Moves the row into dead-letter custody as the drain's attempts gate does.</summary>
    private async Task<Guid> _deadLetterAsync(Guid id) {
      var deadLetterId = (Guid)TrackedGuid.New();
      await using var connection = await _openAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = "SELECT move_to_dead_letters(@dl, 'wh_outbox', @id, @reason, 'publish failed: broker unreachable', @inst, 'test/1')";
      command.Parameters.AddWithValue("dl", deadLetterId);
      command.Parameters.AddWithValue(nameof(id), id);
      command.Parameters.AddWithValue("reason", (int)MessageFailureReason.TransportException);
      command.Parameters.AddWithValue("inst", Guid.CreateVersion7());
      await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(deadLetterId);
      return deadLetterId;
    }

    /// <summary>Runs one scan of the recovery worker, which re-drives every due dead letter.</summary>
    private static async Task _recoverAsync(ServiceProvider provider, Guid deadLetterId) {
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      var worker = new DeadLetterRecoveryWorker(
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        schemaReadyGate: gate,
        options: Options.Create(new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50, EnableGenerationReplay = false }),
        integrityOptions: Options.Create(new StreamIntegrityOptions()),
        generationProvider: new DefaultGenerationProvider(),
        logger: NullLogger<DeadLetterRecoveryWorker>.Instance,
        notificationListener: new Whizbang.Core.Notifications.NoOpWorkNotificationListener());
      var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      worker.OnScanCompleted += () => scanned.TrySetResult();
      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      try {
        await scanned.Task.WaitAsync(OutboxDrainHarness.SignalTimeout);
      } finally {
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
      }
      await Assert.That(worker.TotalRecovered).IsEqualTo(1L)
        .Because($"the scan re-drives dead letter {deadLetterId}");
    }

    private async Task<int> _deadLetterStatusAsync(Guid deadLetterId) {
      await using var connection = await _openAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
      command.Parameters.AddWithValue("id", deadLetterId);
      return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> _openAsync() {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync();
      return connection;
    }

    private (ServiceProvider Provider, System.Text.Json.JsonSerializerOptions JsonOptions) _createService() {
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "ro-sender", "ro-sender-host", 1));
      services.AddScoped(_ => CreateDbContext());
      services.Configure<RoutingOptions>(options => options.OwnDomains(typeof(RecoveredOutboxEvent).Namespace!));

      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      services.AddSingleton(jsonOptions);
      services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
      services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

      services.AddScoped<IWorkCoordinator>(sp =>
        new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
      services.AddScoped<IWorkCoordinatorStrategy>(sp =>
        new ScopedWorkCoordinatorStrategy(
          coordinator: sp.GetRequiredService<IWorkCoordinator>(),
          instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
          workChannelWriter: null,
          options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
          logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
          inboxChannelWriter: new InboxChannelWriter()));
      // The recovery service the Postgres driver registers, and the default recovery policy.
      services.AddScoped<IDeadLetterRecoveryService>(sp =>
        new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()));
      services.AddSingleton<IDeadLetterRecoveryPolicy>(
        new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));

      services.AddReceptors();
      services.AddWhizbangDispatcher();
      services.AddWhizbangLifecycleMessageDeserializer();
      services.AddWhizbangMessageSecurity();
      services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

      return (services.BuildServiceProvider(), jsonOptions);
    }
  }
}
