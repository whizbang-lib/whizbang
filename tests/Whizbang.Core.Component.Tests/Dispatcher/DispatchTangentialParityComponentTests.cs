// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Component.Tests.Tangential.Owned;
using Whizbang.Core.Component.Tests.Tangential.Unowned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The features that ride on a dispatch behave the same on every entry that carries them (#1410
/// follow-up): cancellation, an explicit priority, a payload limit, a generated stream id, the caller's
/// tenant, and the envelope a receptor looks up. Real generated dispatcher; one test per feature, one
/// case per entry.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchTangentialParityComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.Tangential.Owned";

  // ── cancellation ─────────────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("LocalInvokeAsync(void)")]
  [Arguments("LocalInvokeAsync<TResult>")]
  [Arguments("LocalInvokeWithReceiptAsync")]
  [Arguments("LocalSendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  public async Task EveryEntryWithOptions_CanceledBeforeItStarts_RunsAndQueuesNothingAsync(string entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var options = new DispatchOptions { CancellationToken = canceled.Token };

    async Task call() {
      switch (entry) {
        case "SendAsync": await dispatcher.SendAsync(new TgVoidCommand(id), options); break;
        case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { new TgVoidCommand(id) }, options); break;
        case "LocalInvokeAsync(void)": await dispatcher.LocalInvokeAsync((object)new TgVoidCommand(id), options); break;
        case "LocalInvokeAsync<TResult>": await dispatcher.LocalInvokeAsync<ScResponse>((object)new ScRpcCommand(id), options); break;
        case "LocalInvokeWithReceiptAsync": await dispatcher.LocalInvokeWithReceiptAsync<ScResponse>((object)new ScRpcCommand(id), options); break;
        case "LocalSendManyAsync": await dispatcher.LocalSendManyAsync(new object[] { new TgVoidCommand(id) }, options); break;
        case "PublishAsync": await dispatcher.PublishAsync(new TgVoidEvent(id), options); break;
        default: await dispatcher.PublishManyAsync(new object[] { new TgVoidEvent(id) }, options); break;
      }
    }

    await Assert.That(call).Throws<OperationCanceledException>().Because($"{entry} honors a token canceled before it starts");
    await Assert.That(TangentialProbe.Ran("ran", id)).IsFalse();
    await Assert.That(PolicyProbe.Count(nameof(ScRpcCommandReceptor), id)).IsEqualTo(0);
    await Assert.That(outbox.QueuedOf(typeof(TgVoidEvent))).IsEmpty();
  }

  // ── explicit priority and payload limit on the copy ──────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  public async Task EveryEntryThatWritesACopy_StampsTheCallersPriorityOnItAsync(string entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var (message, type) = _copyingMessage(entry, Guid.CreateVersion7());

    await _dispatchWithOptionsAsync(provider.GetRequiredService<IDispatcher>(), entry, message, new DispatchOptions().WithPriority(7));

    var copy = outbox.QueuedOf(type).Single();
    await Assert.That(copy.Priority).IsEqualTo(7).Because($"{entry} carries the caller's priority to the copy");
  }

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  public async Task EveryEntryThatWritesACopy_EnforcesTheCallersPayloadLimitAsync(string entry) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var (message, _) = _copyingMessage(entry, Guid.CreateVersion7());

    await Assert.That(async () => await _dispatchWithOptionsAsync(provider.GetRequiredService<IDispatcher>(), entry, message, new DispatchOptions().WithMaxPayloadBytes(1)))
      .Throws<MessagePayloadTooLargeException>().Because($"{entry} refuses a copy over the caller's limit");
  }

  // ── a generated stream id ────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("LocalInvokeAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  [Arguments("CascadeMessageAsync")]
  public async Task EveryEntry_GeneratesAnEmptyStreamIdBeforeTheReceptorAndTheCopySeeItAsync(string entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var marker = Guid.CreateVersion7();
    var evt = new TgGeneratedStreamEvent { Marker = marker };

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(evt); break;
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)evt); break;
      case "PublishAsync": await dispatcher.PublishAsync(evt); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { evt }); break;
      default: await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, DispatchModes.Both); break;
    }

    var seen = TangentialProbe.Seen("stream", marker);
    await Assert.That(seen).IsNotNull();
    await Assert.That(Guid.Parse(seen!)).IsNotEqualTo(Guid.Empty)
      .Because($"{entry} generates the stream id before the receptor runs");
  }

  // ── the caller's tenant ──────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("LocalInvokeAsync")]
  [Arguments("PublishAsync")]
  public async Task EveryEntryOfTheSecurityBuilder_HandsTheReceptorTheCallersTenantAsync(string entry) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var builder = provider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-parity");
    var id = Guid.CreateVersion7();

    switch (entry) {
      case "SendAsync": await builder.SendAsync(new TgTenantCommand(id)); break;
      case "LocalInvokeAsync": await builder.LocalInvokeAsync(new TgTenantCommand(id)); break;
      default: await builder.PublishAsync(new TgTenantEvent(id)); break;
    }

    await Assert.That(TangentialProbe.Seen("tenant", id)).IsEqualTo("tenant-parity")
      .Because($"{entry} through the security builder runs the receptor in the caller's tenant");
  }

  // ── message security with no caller context ──────────────────────────────────────────────────

  /// <summary>
  /// With message security registered and no caller context, every entry that runs a handler here runs
  /// it the same way. The cascade used to hand the handler's scope a placeholder envelope with no
  /// message, which security refused as a null payload (#1410 follow-up).
  /// </summary>
  [Test]
  [Arguments("SendAsync")]
  [Arguments("PublishAsync")]
  [Arguments("CascadeMessageAsync(Local)")]
  [Arguments("CascadeMessageAsync(Both)")]
  [Arguments("LocalInvokeAsync")]
  public async Task EveryEntry_WithMessageSecurityAndNoCallerContext_RunsTheHandlerAsync(string entry) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox(), withMessageSecurity: true);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(new TgVoidCommand(id)); break;
      case "PublishAsync": await dispatcher.PublishAsync(new TgVoidEvent(id)); break;
      case "CascadeMessageAsync(Local)": await dispatcher.CascadeMessageAsync(new TgVoidEvent(id), sourceEnvelope: null, DispatchModes.Local); break;
      case "CascadeMessageAsync(Both)": await dispatcher.CascadeMessageAsync(new TgVoidEvent(id), sourceEnvelope: null, DispatchModes.Both); break;
      default: await dispatcher.LocalInvokeAsync((object)new TgVoidCommand(id)); break;
    }

    await Assert.That(TangentialProbe.Ran("ran", id)).IsTrue()
      .Because($"{entry} runs the handler here as every other entry does when the caller has no context");
  }

  // ── the envelope a receptor looks up ─────────────────────────────────────────────────────────

  /// <summary>Every entry registers the envelope a receptor looks up for the message it handles.</summary>
  /// <remarks>
  /// A void local invoke with nothing to trace, wait for, or fire afterward takes the zero-allocation path,
  /// which registers no envelope by design (DispatcherBranchCoverageATests pins it); its traced path does.
  /// </remarks>
  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("LocalSendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  [Arguments("CascadeMessageAsync")]
  public async Task EveryEntry_RegistersTheEnvelopeTheReceptorLooksUpAsync(string entry) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(new TgEnvelopeCommand(id)); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { new TgEnvelopeCommand(id) }); break;
      case "LocalSendManyAsync": await dispatcher.LocalSendManyAsync(new object[] { new TgEnvelopeCommand(id) }); break;
      case "PublishAsync": await dispatcher.PublishAsync(new TgEnvelopeEvent(id)); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { new TgEnvelopeEvent(id) }); break;
      default: await dispatcher.CascadeMessageAsync(new TgEnvelopeEvent(id), sourceEnvelope: null, DispatchModes.Both); break;
    }

    await Assert.That(TangentialProbe.Ran("envelope", id)).IsTrue();
    await Assert.That(TangentialProbe.Seen("envelope", id)).IsNotNull()
      .Because($"{entry} registers the envelope a receptor finds for its message, as an event-store append looks it up");
  }

  /// <summary>
  /// The local handlers see the message exactly as its copy was stored and sent (#1455): registering the
  /// envelope they look up does not stamp the message's sent time and correlation a second time.
  /// </summary>
  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendAsync(options)")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishAsync(options)")]
  [Arguments("PublishManyAsync")]
  [Arguments("CascadeMessageAsync")]
  [Arguments("ReceptorResultCascade")]
  [Arguments("PublishAsync(no outbox)")]
  [Arguments("PublishManyAsync(no outbox)")]
  public async Task EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync(string entry) {
    var stored = new StampRecordingEnvelopeSerializer();
    await using var provider = _buildProvider(entry.EndsWith("(no outbox)", StringComparison.Ordinal) ? null : new PolicyRecordingOutbox(), serializer: stored);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var message = new TgStampedEvent { Id = id };

    var command = new TgStampedCommand { Id = id };

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(command); break;
      case "SendAsync(options)": await dispatcher.SendAsync(command, new DispatchOptions()); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { command }); break;
      case "PublishAsync" or "PublishAsync(no outbox)": await dispatcher.PublishAsync(message); break;
      case "PublishAsync(options)": await dispatcher.PublishAsync(message, new DispatchOptions()); break;
      case "PublishManyAsync" or "PublishManyAsync(no outbox)": await dispatcher.PublishManyAsync(new object[] { message }); break;
      case "CascadeMessageAsync": await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Both); break;
      default: await dispatcher.LocalInvokeAsync<TgStampedEvent>(new TgStampingCommand(id)); break;
    }

    var (sentAt, correlationId, hopCorrelationId) = stored.Copies.Single();
    await Assert.That(sentAt).IsNotEqualTo(default).Because($"{entry} stamps the copy it stores");
    await Assert.That(TangentialProbe.Seen("sentAt", id))
      .IsEqualTo(sentAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
      .Because($"{entry}'s handler sees the sent time its stored copy carries");
    await Assert.That(TangentialProbe.Seen("correlation", id)).IsEqualTo(correlationId)
      .Because($"{entry}'s handler sees the correlation its stored copy carries");
    await Assert.That(correlationId).IsEqualTo(hopCorrelationId)
      .Because($"{entry}'s stored copy carries the correlation of the envelope it travels in");
  }

  /// <summary>A local run with no copy to stamp the message stamps it itself, once (#1455).</summary>
  [Test]
  [Arguments("CascadeMessageAsync(LocalNoPersist)")]
  public async Task EveryLocalRunWithNoCopy_StampsTheMessageItselfAsync(string entry) {
    var stored = new StampRecordingEnvelopeSerializer();
    await using var provider = _buildProvider(new PolicyRecordingOutbox(), serializer: stored);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var message = new TgStampedEvent { Id = id };

    await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.LocalNoPersist);

    await Assert.That(stored.Copies).IsEmpty().Because($"{entry} writes no copy");
    await Assert.That(TangentialProbe.Seen("sentAt", id))
      .IsNotEqualTo(default(DateTimeOffset).ToString("O", System.Globalization.CultureInfo.InvariantCulture))
      .Because($"{entry}'s handler sees the message stamped");
    await Assert.That(TangentialProbe.Seen("correlation", id)).IsNotNull()
      .Because($"{entry}'s handler sees the message's correlation");
  }

  /// <summary>Records what each stamped event carried at the moment its copy was serialized for storing.</summary>
  private sealed class StampRecordingEnvelopeSerializer : IEnvelopeSerializer {
    private readonly PolicyContextKeepingEnvelopeSerializer _inner = new();

    public System.Collections.Concurrent.ConcurrentQueue<(DateTimeOffset SentAt, string? CorrelationId, string? HopCorrelationId)> Copies { get; } = new();

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var hopCorrelation = envelope.Hops[^1].CorrelationId?.Value.ToString();
      if (envelope.Payload is TgStampedEvent stamped) {
        Copies.Enqueue((stamped.SentAt, stamped.CorrelationId, hopCorrelation));
      } else if (envelope.Payload is TgStampedCommand stampedCommand) {
        Copies.Enqueue((stampedCommand.SentAt, stampedCommand.CorrelationId, hopCorrelation));
      }
      return _inner.SerializeEnvelope(envelope);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      _inner.DeserializeMessage(jsonEnvelope, messageTypeName);
  }

  // ── a local invoke under a selected policy that defers the run ───────────────────────────────

  /// <summary>
  /// A local invoke whose caller selects a policy that sends the message on and schedules it runs
  /// nothing now and writes one copy carrying the schedule and the deferred run (#1410 follow-up).
  /// </summary>
  [Test]
  public async Task LocalInvokeUnderTheSendPolicy_Scheduled_RunsNothingNowAndWritesTheDeferredCopyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();
    var scheduledAt = new DateTimeOffset(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)new PbUnownedCommand(id),
      new DispatchOptions().WithPolicy(Whizbang.Core.Dispatch.Policy.DispatchPolicies.Send.Id).WithScheduledFor(scheduledAt));

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), id)).IsEqualTo(0);
    var copy = outbox.QueuedOf(typeof(PbUnownedCommand)).Single();
    await Assert.That(copy.ScheduledFor).IsEqualTo(scheduledAt);
    await Assert.That(copy.Envelope.DispatchContext.LocalDeferred).IsTrue();
  }

  // ── what a batch's handlers wrote, when the batch stops or runs in a unit of work ────────────

  /// <summary>
  /// A batch that stops at its first message, before writing any copy, still writes what that message's
  /// handler emitted before it failed, and nothing of the messages after it (#1410 follow-up).
  /// </summary>
  [Test]
  public async Task SendMany_StoppedBeforeItsFirstCopy_StillWritesWhatTheFailedHandlerEmittedAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>()
        .SendManyAsync([new TgEmitThenFailCommand(id), new PbUnownedCommand(Guid.CreateVersion7())]))
      .Throws<InvalidOperationException>();

    await Assert.That(outbox.QueuedOf(typeof(TgVoidEvent))).Count().IsEqualTo(1)
      .Because("the event the failed handler published is written, after the batch's own copies, of which there are none");
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty()
      .Because("the batch stopped, so the message after the failure is neither run nor written");
  }

  /// <summary>The same batch whose failed handler emitted nothing writes nothing at all.</summary>
  [Test]
  public async Task SendMany_StoppedBeforeItsFirstCopy_WithNothingEmitted_WritesNothingAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>()
        .SendManyAsync([new TgFailCommand(Guid.CreateVersion7()), new PbUnownedCommand(Guid.CreateVersion7())]))
      .Throws<InvalidOperationException>();

    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty();
    await Assert.That(outbox.QueuedOf(typeof(TgVoidEvent))).IsEmpty();
  }

  /// <summary>
  /// A batch inside a unit of work hands it the batch's copies first and then what the handlers emitted,
  /// so the cause precedes its effect in the unit's commit too (#1410 follow-up).
  /// </summary>
  [Test]
  public async Task PublishMany_InsideAUnitOfWork_HandsItTheCauseBeforeItsEffectAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var id = Guid.CreateVersion7();

    using var unitOfWork = DispatchOutboxCollector.BeginCollecting();
    await provider.GetRequiredService<IDispatcher>().PublishManyAsync(new object[] { new TgCauseEvent(id) });

    var collected = unitOfWork.Collector.Collected.Select(m => m.MessageType).ToList();
    await Assert.That(collected).Count().IsEqualTo(2);
    await Assert.That(collected[0]).Contains(nameof(TgCauseEvent));
    await Assert.That(collected[1]).Contains(nameof(TgVoidEvent));
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  /// <summary>A message whose dispatch on this entry writes a copy: unowned commands for sends, events for publishes.</summary>
  private static (object Message, Type Type) _copyingMessage(string entry, Guid id) =>
    entry.StartsWith("Send", StringComparison.Ordinal)
      ? (new PbUnownedCommand(id), typeof(PbUnownedCommand))
      : (new TgVoidEvent(id), typeof(TgVoidEvent));

  private static Task _dispatchWithOptionsAsync(IDispatcher dispatcher, string entry, object message, DispatchOptions options) => entry switch {
    "SendAsync" => dispatcher.SendAsync(message, options),
    "SendManyAsync" => dispatcher.SendManyAsync([message], options),
    "PublishAsync" => dispatcher.PublishAsync(message, options),
    _ => dispatcher.PublishManyAsync([message], options),
  };

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy? outbox, bool withMessageSecurity = false,
      IEnvelopeSerializer? serializer = null) {
    var services = new ServiceCollection();
    if (withMessageSecurity) {
      services.AddWhizbangMessageSecurity();
      services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    }
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (outbox is not null) {
      services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    }
    services.AddSingleton(serializer ?? new PolicyContextKeepingEnvelopeSerializer());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
