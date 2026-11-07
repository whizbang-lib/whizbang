// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2092 and #2098 end to end on a real store: a queued backfill of a type with more events than the owner's answer
/// bound receives every event, across several windows, each requested only after the previous one answered, and ends
/// complete. The requester's paced worker, the owner's redelivery receptor and the requester's answer receptor are the
/// production ones; only the transport between them is the test, which carries each message by hand.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionBackfillWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BackfillWindowAnswerReceptor.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RedeliveryBuildGate")]   // the owner receptor's per-process build gate is shared state
[Category("Shard4")]
public class PacedBackfillEndToEndTests : EFCoreTestBase {
  private const string TYPE = "Contracts.PacedThing";
  private static readonly DateTimeOffset _start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

  [Test]
  [Timeout(120_000)]
  public async Task ATypeLargerThanTheOwnerBound_ArrivesWhole_AcrossSeveralWindows_AndTheBackfillCompletesAsync(CancellationToken testToken) {
    await using var seedContext = CreateDbContext();
    var conn = (NpgsqlConnection)seedContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync(testToken);
    }
    var seeded = new List<Guid>();
    for (var i = 1; i <= 7; i++) {
      seeded.Add(await _seedAsync(conn, TrackedGuid.New().Value, i));
    }
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var serializer = new EnvelopeSerializer(jsonOptions);
    var clock = new FakeTimeProvider(_start);
    var integrity = Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });

    // The requesting service: its paced worker and its answer receptor, over the shared store.
    var requests = new ChannelTransport();
    var requester = new ServiceCollection();
    requester.AddScoped<IWorkCoordinator>(_ => _coordinator());
    requester.AddSingleton<ITransport>(requests);
    requester.AddSingleton<IEnvelopeSerializer>(serializer);
    requester.AddSingleton<IServiceInstanceProvider>(new NamedInstance("billing-svc"));
    requester.AddSingleton<TimeProvider>(clock);
    requester.AddSingleton(integrity);
    var consumer = new TransportConsumerOptions();
    consumer.Destinations.Add(new TransportDestination("inbox"));
    requester.AddSingleton(consumer);
    await using var requesterServices = requester.BuildServiceProvider();
    using var requesterScope = requesterServices.CreateScope();
    var coordinator = requesterScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.EnqueueSubscriptionBackfillsAsync([new SubscriptionBackfillSeed(Guid.Empty, string.Empty, TYPE)], testToken);
    var answerReceptor = new BackfillWindowAnswerReceptor(
      requesterServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackfillWindowAnswerReceptor>.Instance);

    // The owning service: its redelivery receptor, whose answer bound (3) is smaller than the type (7 events).
    var answers = new ChannelTransport();
    var owner = new ServiceCollection();
    owner.AddScoped<IWorkCoordinator>(_ => _coordinator());
    owner.AddSingleton<ITransport>(answers);
    owner.AddSingleton<IEnvelopeSerializer>(serializer);
    owner.AddSingleton<IServiceInstanceProvider>(new NamedInstance("orders-svc"));
    owner.AddSingleton(new RedeliveryPumpOptions { MaxEventsPerRequest = 3, SelectPageSize = 2 });
    owner.AddSingleton(integrity);
    await using var ownerServices = owner.BuildServiceProvider();
    var ownerReceptor = new RedeliveryRequestReceptor(
      ownerServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    using var worker = new SubscriptionBackfillWorker(
      requesterServices.GetRequiredService<IServiceScopeFactory>(), integrity, NullLogger<SubscriptionBackfillWorker>.Instance, clock);
    await worker.StartAsync(testToken);
    var delivered = new List<Guid>();
    var windows = 0;
    try {
      BackfillWindowAnswer answer;
      do {
        // A minute of the worker's clock buys a window; the worker asks only once the last one has answered.
        clock.Advance(TimeSpan.FromMinutes(1));
        var request = _payload<RequestRedeliveryCommand>(await requests.Published.Reader.ReadAsync(testToken), jsonOptions);
        windows++;
        await ownerReceptor.HandleAsync(request, testToken);
        answer = await _drainOwnerAsync(answers, jsonOptions, delivered, testToken);
        await answerReceptor.HandleAsync(answer, testToken);
      } while (answer.HasMore && windows < 10);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(windows).IsEqualTo(3).Because("3 + 3 + 1: every window stops at the owner's bound of 3 and says so (#2098)");
    await Assert.That(delivered).IsEquivalentTo(seeded).Because("every event of the type arrives, each exactly once");
    var progress = (await coordinator.GetSubscriptionBackfillsAsync(testToken)).Single();
    await Assert.That(progress.Outcome).IsEqualTo(SubscriptionBackfillOutcome.Complete);
    await Assert.That(progress.EventsReceived).IsEqualTo(7L);
    await Assert.That(progress.WindowsRequested).IsEqualTo(3L);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() =>
    new(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

  /// <summary>Takes the owner's bundles for one window, recording their events, up to and including its answer.</summary>
  private static async Task<BackfillWindowAnswer> _drainOwnerAsync(
      ChannelTransport answers, JsonSerializerOptions jsonOptions, List<Guid> delivered, CancellationToken cancellationToken) {
    while (true) {
      var (envelope, envelopeType) = await answers.Published.Reader.ReadAsync(cancellationToken);
      if (envelopeType!.Contains(nameof(BackfillWindowAnswer), StringComparison.Ordinal)) {
        return _payload<BackfillWindowAnswer>((envelope, envelopeType), jsonOptions);
      }
      delivered.AddRange(_payload<RedeliveryComposite>((envelope, envelopeType), jsonOptions).InnerEventIds);
    }
  }

  private static T _payload<T>((IMessageEnvelope Envelope, string? EnvelopeType) published, JsonSerializerOptions jsonOptions) =>
    (T)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)published.Envelope).Payload.GetRawText(), jsonOptions.GetTypeInfo(typeof(T)))!;

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, Guid streamId, int version) {
    var eventId = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = @"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, NULL, @version, nextval('wh_commit_seq'), 0)";
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", TYPE);
      store.Parameters.AddWithValue(nameof(version), version);
      await store.ExecuteNonQueryAsync();
    }
    await using var body = conn.CreateCommand();
    body.CommandText = "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@event, '{\"seeded\":true}'::jsonb, '{}'::jsonb)";
    body.Parameters.AddWithValue("event", eventId);
    await body.ExecuteNonQueryAsync();
    return eventId;
  }

  private sealed class ChannelTransport : ITransport {
    public Channel<(IMessageEnvelope Envelope, string? EnvelopeType)> Published { get; } =
      Channel.CreateUnbounded<(IMessageEnvelope, string?)>();
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) =>
      Published.Writer.WriteAsync((envelope, envelopeType), cancellationToken).AsTask();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class NamedInstance(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
