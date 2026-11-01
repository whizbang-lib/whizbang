// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1901: the configured dead-letter level reaches every dead-letter path the framework builds through a factory,
/// where nothing hands it the options unless the registration does: the inbox and outbox flush failure recorders
/// (registered by <c>AddWhizbangWorkers</c>) and the transport consumer (registered by <c>AddTransportConsumer</c>,
/// which a host can use without the worker pipeline).
/// </summary>
/// <docs>operations/configuration/configuration-reference#dead-letter-logging</docs>
[Category("Workers")]
public sealed class DeadLetterLoggingWiringTests {
  private static IConfiguration _warning() => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:DeadLetterLogging:Level"] = "Warning" })
    .Build();

  [Test]
  public async Task AddWhizbangWorkers_ConfiguredWarning_TheFlushRecordersLogTheRecordAtWarningAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(_warning());
    services.AddLogging(b => b.AddFakeLogging());
    services.AddScoped<IWorkCoordinator, DeadLetteringCoordinator>();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();
    var collector = provider.GetFakeLogCollector();

    await provider.GetRequiredService<InboxFlushFailureRecorder>().RecordAsync(
      new InboxFlushFailure(Guid.Empty, [_inboxMessage()], InboxFlushFailureCause.NotRetryable, 1, new InvalidOperationException("refused")),
      CancellationToken.None);
    await provider.GetRequiredService<OutboxFlushFailureRecorder>().RecordAsync(
      new OutboxFlushFailure(Guid.Empty, [_outboxMessage()], OutboxFlushFailureCause.NotRetryable, 1, new InvalidOperationException("refused")),
      CancellationToken.None);

    var records = collector.GetSnapshot().Where(r => r.Id.Id == DeadLetterLog.EVENT_ID).ToList();
    await Assert.That(records).Count().IsEqualTo(2);
    await Assert.That(records.All(r => r.Level == LogLevel.Warning)).IsTrue()
      .Because("the recorders are built by a factory, which must hand them the bound option or the setting does nothing");
  }

  [Test]
  public async Task AddTransportConsumer_ConfiguredWarning_TheConsumerLogsDeadLettersAtWarningAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(_warning());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider());
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    services.AddSingleton<TransportMetrics>();
    services.AddSingleton<ITransport>(new InProcessTransport(new FakeTimeProvider()));
    new WhizbangBuilder(services).WithRouting(_ => { }).AddTransportConsumer();
    await using var provider = services.BuildServiceProvider();

    var worker = provider.GetRequiredService<TransportConsumerWorker>();

    await Assert.That(worker.DeadLetterLevel).IsEqualTo(LogLevel.Warning)
      .Because("the consumer is built by a factory without the worker pipeline, so its registration binds and hands it the option");
  }

  private static InboxMessage _inboxMessage() {
    var id = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "test",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    };
  }

  private static OutboxMessage _outboxMessage() {
    var id = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse("{}"), []),
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }

  /// <summary>Keeps every batch it is asked to dead-letter.</summary>
  private sealed class DeadLetteringCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public Task<bool> StoreInboxDeadLettersAsync(InboxMessage[] messages, InboxDeadLetterRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public Task<bool> StoreOutboxDeadLettersAsync(OutboxMessage[] messages, OutboxDeadLetterRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
  }
}
