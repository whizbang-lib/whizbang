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

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  /// <summary>A message whose dispatch on this entry writes a copy: unowned commands for sends, events for publishes.</summary>
  private static (object Message, Type Type) _copyingMessage(string entry, Guid id) =>
    entry.StartsWith("Send", StringComparison.Ordinal)
      ? (new PbUnownedCommand(id), typeof(PbUnownedCommand))
      : (new TgVoidEvent(id), typeof(TgVoidEvent));

  private static Task _dispatchWithOptionsAsync(IDispatcher dispatcher, string entry, object message, DispatchOptions options) => entry switch {
    "SendAsync" => dispatcher.SendAsync(message, options),
    "SendManyAsync" => dispatcher.SendManyAsync(new[] { message }, options),
    "PublishAsync" => dispatcher.PublishAsync(message, options),
    _ => dispatcher.PublishManyAsync(new[] { message }, options),
  };

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
