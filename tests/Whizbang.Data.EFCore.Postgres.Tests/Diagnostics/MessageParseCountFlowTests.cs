// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Testing.Containers;
using Whizbang.Testing.Lifecycle;
using Whizbang.Testing.MultiService;

namespace Whizbang.Data.EFCore.Postgres.Tests.Diagnostics;

/// <summary>
/// How many times one message is parsed on its way through a real service: a host started the way a consumer
/// starts one (the public <c>AddWhizbang</c> chain on Postgres), joined to a wire whose boundary is real JSON bytes,
/// with one event applied by two perspectives and a receptor at the last lifecycle stage. Each parse the framework
/// makes is reported through <see cref="MessageParseDiagnostics"/> with its site, so the counts here are the
/// measurement behind the runtime message cache's design and the pins each of its migrations must lower, never
/// raise.
/// </summary>
/// <remarks>
/// <para>
/// Each test waits on the exact transitions it measures (both perspectives applied to the stream, and the last
/// lifecycle stage fired for the event), then stops the host, which returns only once every worker has finished;
/// no parse of the measured message can happen after the counts are read.
/// </para>
/// <para>
/// The counts are what the code does today. A change that lowers one updates the pin in the same pull request and
/// says why; a change that raises one is a regression unless its pull request argues otherwise.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/MessageParseDiagnostics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public class MessageParseCountFlowTests {
  private const string SHARED_TOPIC = "inbox";
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("parsecount");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    Npgsql.NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>A publish from this service: dispatcher, outbox, wire, and the event store feeding two perspectives.</summary>
  [Test]
  [Timeout(180000)]
  public async Task LocalPublish_OneEventTwoPerspectives_ParseCountsAsync(CancellationToken cancellationToken) {
    var wire = new RecordingWire(new InMemoryWireTransport(JsonContextRegistry.CreateCombinedOptions()));
    using var recorder = new MessageParseRecorder();
    var streamId = TrackedGuid.New().Value;
    Guid messageId;

    using (var host = _buildHost(wire)) {
      await _startAsync(host, cancellationToken);
      var done = _awaitFlow(host, streamId);

      var receipt = await host.Services.GetRequiredService<IDispatcher>().PublishAsync(_event(streamId));
      messageId = receipt.MessageId.Value;

      await done.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
      await host.StopAsync(cancellationToken);
    }

    _report("local publish", messageId, recorder, wire);
    var summary = recorder.Summarize(messageId);
    await Assert.That(summary.Select(s => s.Source).Distinct().ToList())
      .IsEquivalentTo([MessageParseSource.LifecycleStage, MessageParseSource.EventStoreRead])
      .Because("a local event is stored and drained; it never leaves on the wire in this topology");
    await Assert.That(_count(summary, MessageParseSource.LifecycleStage, MessageParseKind.Payload)).IsEqualTo(5)
      .Because("each of the five distribute stages parses the payload again (plans/message-cache.md, migration step 1)");
    await Assert.That(_count(summary, MessageParseSource.EventStoreRead, MessageParseKind.Payload)).IsEqualTo(4)
      .Because("the drain parses one row per perspective (2), and the refetch reads both applied rows again (2) (step 2)");
    await Assert.That(_count(summary, MessageParseSource.EventStoreRead, MessageParseKind.Metadata)).IsEqualTo(4);
    await Assert.That(_count(summary, MessageParseSource.EventStoreRead, MessageParseKind.Scope)).IsEqualTo(4);
    await Assert.That(recorder.CountFor(messageId)).IsEqualTo(17);
  }

  private static int _count(IReadOnlyList<MessageParseSiteCount> summary, MessageParseSource source, MessageParseKind kind) =>
    summary.Where(s => s.Source == source && s.Kind == kind).Sum(s => s.Count);

  private static ConsumerFanoutEvent _event(Guid streamId) => new() {
    StreamId = streamId,
    Title = "parse-count",
    ConditionLabels = ["a", "b", "c"]
  };

  private static void _report(string flow, Guid messageId, MessageParseRecorder recorder, RecordingWire wire) {
    var text = new StringBuilder();
    text.AppendLine(CultureInfo.InvariantCulture, $"PARSECOUNT flow={flow} message={messageId} total={recorder.CountFor(messageId)}");
    foreach (var line in recorder.Summarize(messageId)) {
      text.AppendLine(CultureInfo.InvariantCulture, $"PARSECOUNT   {line.Kind,-9} {line.Source,-18} {line.Site,-40} x{line.Count}");
    }
    foreach (var destination in wire.SubscribedDestinations) {
      text.AppendLine(CultureInfo.InvariantCulture, $"PARSECOUNT   subscribed {destination.Address}/{destination.RoutingKey}");
    }
    foreach (var (destination, type) in wire.Published) {
      text.AppendLine(CultureInfo.InvariantCulture, $"PARSECOUNT   published {destination.Address}/{destination.RoutingKey} {type}");
    }
    Console.WriteLine(text.ToString());
  }

  /// <summary>Completes when both perspectives have applied the stream and the last lifecycle stage has fired for it.</summary>
  private static Task _awaitFlow(IHost host, Guid streamId) {
    var perspectives = host.Services.GetServices<IHostedService>().OfType<PerspectiveWorker>().Single();
    var applied = new ConcurrentDictionary<string, byte>();
    var bothApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    perspectives.OnPerspectiveEventProcessed += e => {
      if (e.StreamId == streamId && applied.TryAdd(e.PerspectiveName, 0) && applied.Count == 2) {
        bothApplied.TrySetResult();
      }
    };
    // The awaiter's receptor lives in the testing package, so this assembly's receptor discovery does not register it.
    var lastStage = new LifecycleStageAwaiter<ConsumerFanoutEvent>(
      host, LifecycleStage.PostLifecycleInline, messageFilter: m => m.StreamId == streamId);
    return Task.WhenAll(bothApplied.Task, lastStage.WaitAsync(TimeSpan.FromSeconds(60)));
  }

  private IHost _buildHost(ITransport transport) {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:db"] = _database.ConnectionString });
    builder.Logging.ClearProviders();
    builder.Services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    builder.Services.AddSingleton(transport);
    // One shared topic in and out, as a service on a shared-topic topology runs: a publish leaves on the wire, and a
    // delivery from another service arrives on it.
    _ = builder.Services.AddWhizbang()
      .WithRouting(static r => {
        r.Inbox.UseSharedTopic(SHARED_TOPIC);
        r.Outbox.UseSharedTopic(SHARED_TOPIC);
      })
      .WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres
      .AddTransportConsumer();
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    return builder.Build();
  }

  private static async Task _startAsync(IHost host, CancellationToken cancellationToken) {
    await host.StartAsync(cancellationToken);
    await host.Services.GetRequiredService<IStartupReadySignal>().WaitForReadyAsync(cancellationToken);
  }

  /// <summary>The wire, with a record of what the host subscribed to and published.</summary>
  private sealed class RecordingWire(InMemoryWireTransport inner) : ITransport {
    private readonly ConcurrentQueue<TransportDestination> _subscribed = new();
    private readonly ConcurrentQueue<(TransportDestination, string?)> _published = new();

    public InMemoryWireTransport Inner { get; } = inner;
    public IReadOnlyList<TransportDestination> SubscribedDestinations => [.. _subscribed];
    public IReadOnlyList<(TransportDestination Destination, string? EnvelopeType)> Published => [.. _published];
    public bool IsInitialized => Inner.IsInitialized;
    public TransportCapabilities Capabilities => Inner.Capabilities;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Inner.InitializeAsync(cancellationToken);

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      _published.Enqueue((destination, envelopeType));
      return Inner.PublishAsync(envelope, destination, envelopeType, preSerializedBytes, cancellationToken);
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      _subscribed.Enqueue(destination);
      return Inner.SubscribeBatchAsync(batchHandler, destination, batchOptions, cancellationToken);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
      Inner.SendAsync<TRequest, TResponse>(requestEnvelope, destination, cancellationToken);
  }
}
