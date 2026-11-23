// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.DerivedSurface.Owned;
using Whizbang.Core.Component.Tests.DerivedSurface.Unowned;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tests.Shutdown;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Tracing;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The ids a handling derives (#2543) pass the time-ordering check every event and message id entry point runs (#2528)
/// with no opt-out: a derived id is a version 7 id by construction (<c>DerivedIdentity</c> stamps the version), so the
/// check, which reads only the version, accepts it silently. Run at <see cref="GuidOrderingSeverity.Warning"/>, where a
/// violation would be logged and counted instead of thrown, so a regression shows as a count rather than a refusal.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
[Category("Dispatcher")]
[Category("GuidOrdering")]
public class DerivedIdentityTimeOrderingComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.DerivedSurface.Owned";
  private const string HANDLER = "TimeOrderingHandler";

  /// <summary>A version 4 id, the shape the entry check reports, for the positive control.</summary>
  private static readonly Guid _aVersion4Id = new([0x5f, 0x0c, 0x1d, 0x2e, 0x3a, 0x4b, 0x4c, 0x5d, 0x9e, 0x6f, 0x7a, 0x8b, 0x9c, 0x0d, 0x1e, 0x2f], bigEndian: true);

  [Test]
  public async Task DerivedIds_QueuedThroughTheStrategyEntryCheck_AreNeitherLoggedNorCountedAsync() {
    using var factory = new TestMeterFactory();
    var probe = _build(factory);
    var source = (Guid)TrackedGuid.New();

    var firstRun = await _dispatchInAHandlingAsync(probe, source);
    var redelivery = await _dispatchInAHandlingAsync(probe, source);

    await Assert.That(firstRun.Count).IsGreaterThanOrEqualTo(8)
      .Because("every copy the handling wrote went through the strategy's entry check, or the test checks nothing");
    await Assert.That(redelivery).IsEquivalentTo(firstRun, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("precondition: these are derived ids, the ones a redelivery writes again");
    await Assert.That(probe.Logger.Entries).IsEmpty()
      .Because("a derived id is a version 7 id: the entry check accepts it without a report");
    await Assert.That(ProbeMeterReader.ReadTotal(factory.CreatedMeters[0], GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(0)
      .Because("and counts no violation for it");
  }

  [Test]
  public async Task ThePositiveControl_ANonV7IdThroughTheSameEntryCheck_IsLoggedAndCountedAsync() {
    using var factory = new TestMeterFactory();
    var probe = _build(factory);

    using (var scope = probe.Services.CreateScope()) {
      await scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>().QueueOutboxMessageAsync(new OutboxMessage {
        MessageId = _aVersion4Id,
        Destination = "topic",
        Envelope = new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = new MessageId(_aVersion4Id),
          Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { probe = true }),
          Hops = [],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        },
        Metadata = new EnvelopeMetadata { MessageId = new MessageId(_aVersion4Id), Hops = [] },
        EnvelopeType = "env",
        MessageType = "App.Contracts.Probe, App.Contracts",
        StreamId = (Guid)TrackedGuid.New(),
        IsEvent = true,
      });
    }

    await Assert.That(probe.Logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
      .Because("the same wiring reports a version 4 id, so the silence above is the check passing, not the check missing");
    await Assert.That(ProbeMeterReader.ReadTotal(factory.CreatedMeters[0], GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(1);
  }

  [Test]
  public async Task EveryDerivedIdKind_FromANonV7Cause_PassesTheCheckSilentlyAsync() {
    using var factory = new TestMeterFactory();
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(
      new WhizbangOptions { GuidOrderingViolationSeverity = GuidOrderingSeverity.Warning }, logger,
      new GuidOrderingMetrics(new WhizbangMetrics(factory)));
    // A client-supplied version 4 cause: the derived ids still carry version 7, so no entry point reports them.
    var cause = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");
    Guid[] derived = [
      EmissionIdentity.Derive(cause, "orders", HANDLER, "App.Contracts.Placed, App.Contracts", 0),
      SentMessageIdentity.Derive(cause, "orders", HANDLER, "App.Contracts.Ship, App.Contracts", 0),
      GeneratedStreamIdentity.DeriveInHandling(cause, "orders", HANDLER, "App.Contracts.Ship, App.Contracts", 0),
    ];

    foreach (var id in derived) {
      validator.ValidateMessageId(id, "App.Contracts.Placed, App.Contracts", "Test.EntryPoint");
    }

    await Assert.That(derived.All(static id => id.Version == 7)).IsTrue();
    await Assert.That(logger.Entries).IsEmpty();
    await Assert.That(ProbeMeterReader.ReadTotal(factory.CreatedMeters[0], GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(0);
  }

  // ---- helpers ----

  private static async Task<List<Guid>> _dispatchInAHandlingAsync(Probe probe, Guid source) {
    probe.Queued.Clear();
    var dispatcher = probe.Services.GetRequiredService<IDispatcher>();
    using (ReceptorHandling.Enter(_handling(source))) {
      await dispatcher.SendAsync(new DsCascadedCommand((Guid)TrackedGuid.New()));
      await dispatcher.PublishAsync(new DsEvent((Guid)TrackedGuid.New()));
      await dispatcher.PublishManyAsync(new object[] { new DsEvent((Guid)TrackedGuid.New()), new DsEvent((Guid)TrackedGuid.New()) });
      await dispatcher.SendManyAsync(new object[] { new DsCascadedCommand((Guid)TrackedGuid.New()), new DsCascadedCommand((Guid)TrackedGuid.New()) });
      await dispatcher.CascadeMessageAsync(new DsCascadedCommand((Guid)TrackedGuid.New()), sourceEnvelope: null, DispatchModes.Outbox);
      await dispatcher.CascadeMessageAsync(new DsEvent((Guid)TrackedGuid.New()), sourceEnvelope: null, DispatchModes.Outbox);
    }
    return [.. probe.Queued];
  }

  private sealed record Probe(IServiceProvider Services, CollectingLogger Logger, List<Guid> Queued);

  private static Probe _build(TestMeterFactory factory) {
    var logger = new CollectingLogger();
    var queued = new List<Guid>();
    var validator = new GuidOrderingValidator(
      new WhizbangOptions { GuidOrderingViolationSeverity = GuidOrderingSeverity.Warning }, logger,
      new GuidOrderingMetrics(new WhizbangMetrics(factory)));
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(validator);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddScoped<IWorkCoordinatorStrategy>(sp => new RecordingStrategy(queued, new ImmediateWorkCoordinatorStrategy(
      coordinator: new NoOpWorkCoordinator(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      options: new WorkCoordinatorOptions(),
      logger: NullLogger<ImmediateWorkCoordinatorStrategy>.Instance,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer(),
      tracingOptions: sp.GetRequiredService<IOptionsMonitor<TracingOptions>>(),
      deferredChannel: new DeferredOutboxChannel(),
      systemEventOptions: Options.Create(new SystemEventOptions()),
      workChannelWriter: new WorkChannelWriter(),
      auditDecisionHook: null,
      detachedWork: sp.GetRequiredService<DetachedLifecycleWork>(),
      guidOrdering: validator)));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return new Probe(services.BuildServiceProvider(), logger, queued);
  }

  /// <summary>The envelope a worker hands a receptor for one handler row; a redelivery builds a new one with the same values.</summary>
  private static MessageEnvelope<object> _handling(Guid sourceMessageId) => new() {
    MessageId = MessageId.From(sourceMessageId),
    Payload = new DsAnswer(sourceMessageId),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = HANDLER },
  };

  /// <summary>Records each outbox message's id, then hands it to the real strategy, whose entry check runs on it.</summary>
  private sealed class RecordingStrategy(List<Guid> queued, IWorkCoordinatorStrategy inner) : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) {
      inner.QueueOutboxMessage(message);
      lock (queued) {
        queued.Add(message.MessageId);
      }
    }

    public void QueueInboxMessage(InboxMessage message) => inner.QueueInboxMessage(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) => inner.QueueOutboxCompletion(messageId, completedStatus);
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) => inner.QueueInboxCompletion(messageId, completedStatus);
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      inner.QueueOutboxFailure(messageId, completedStatus, errorMessage);
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      inner.QueueInboxFailure(messageId, completedStatus, errorMessage);
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => inner.FlushAsync(flags, ct);
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) => inner.FlushAndGetBatchAsync(flags, ct);
  }

  /// <summary>Every log entry the validator writes.</summary>
  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }
}
