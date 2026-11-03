// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// #1996: a raw receptor is a receptor, so the registered receptor interceptors are asked before it runs, with the
/// message's envelope and the scope the envelope carries. The message is already stored when the raw receptor runs
/// (#1720), so the broker settlement carries the decision: DropQuiet skips the raw receptor and completes the message;
/// DeadLetter and Quarantine dead-letter it at the broker under their own reasons, with no redelivery; Throw fails the
/// delivery, so the broker's redelivery policy retries it.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
[Category("Security")]
[Timeout(10_000)]
public class AzureServiceBusRawReceptorInterceptorTests {
  private const string RAW_INNER_TYPE_NAME = "Contracts.External.RawOnlyMessage, Contracts.External";
  private const string RAW_ENVELOPE_TYPE_NAME =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Contracts.External.RawOnlyMessage, Contracts.External]], Whizbang.Core";

  [Test]
  public async Task RawReceptor_Allowed_RunsAndCompletesAsync() {
    var interceptor = new FixedInterceptor(InterceptorResult.Allowed);
    var (receptor, receiver) = await _deliverAsync(interceptor, expectCompleted: true);

    await Assert.That(receptor.Handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(interceptor.ReceptorTypes).Contains(typeof(RecordingRawReceptor))
      .Because("the interceptor is asked about the raw receptor's own class");
  }

  [Test]
  public async Task RawReceptor_DropQuiet_IsSkippedAndTheMessageCompletesAsync() {
    var (receptor, receiver) = await _deliverAsync(new FixedInterceptor(InterceptorResult.Deny(DeniedAction.DropQuiet)), expectCompleted: true);

    await Assert.That(receptor.Handled).IsEmpty();
    await Assert.That(receiver.Completed).Count().IsEqualTo(1).Because("a quiet drop acknowledges the message");
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  [Test]
  [Arguments(DeniedAction.DeadLetter, "ReceptorDenied")]
  [Arguments(DeniedAction.Quarantine, "ReceptorQuarantined")]
  public async Task RawReceptor_DeadLetterOrQuarantine_DeadLettersWithoutRedeliveryAsync(DeniedAction action, string reason) {
    var (receptor, receiver) = await _deliverAsync(
      new FixedInterceptor(InterceptorResult.Deny(action, "Missing required permission: orders:approve")), expectCompleted: false);

    await Assert.That(receptor.Handled).IsEmpty();
    var (_, deadLetterReason, description) = receiver.DeadLettered.Single();
    await Assert.That(deadLetterReason).IsEqualTo(reason);
    await Assert.That(description).Contains("orders:approve");
    await Assert.That(receiver.Abandoned).IsEmpty().Because("a dead-lettered denial is not redelivered");
    await Assert.That(receiver.Completed).IsEmpty();
  }

  [Test]
  public async Task RawReceptor_Throw_AbandonsForRedeliveryAsync() {
    var (receptor, receiver) = await _deliverAsync(new FixedInterceptor(InterceptorResult.Deny(DeniedAction.Throw)), expectCompleted: false);

    await Assert.That(receptor.Handled).IsEmpty();
    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }

  [Test]
  public async Task RawReceptor_NoInterceptors_RunsAsBeforeAsync() {
    var (receptor, receiver) = await _deliverAsync(interceptor: null, expectCompleted: true);

    await Assert.That(receptor.Handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }

  private static async Task<(RecordingRawReceptor Receptor, RecordingTransportReceiver Receiver)> _deliverAsync(
      IReceptorInterceptor? interceptor, bool expectCompleted) {
    var receptor = new RecordingRawReceptor(RAW_INNER_TYPE_NAME);
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false, MaxDeliveryAttempts = 10 },
      (ILogger<AzureServiceBusTransport>?)NullLogger<AzureServiceBusTransport>.Instance,
      rawReceptorRegistry: new SingleRawReceptorRegistry(receptor),
      typeBinder: new MissTypeBinder(),
      receptorInterceptors: interceptor is null ? null : new ReceptorInterceptorPipeline([interceptor]));
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, new TransportDestination("batch-topic", "batch-sub"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 600_000, MaxWaitMs = 600_000 });
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.RawMessage(_rawEnvelopeBody(), RAW_ENVELOPE_TYPE_NAME), receiver));
    await (expectCompleted ? receiver.CompletedSignal.Task : receiver.NotCompletedSignal.Task);
    return (receptor, receiver);
  }

  private static string _rawEnvelopeBody() {
    var envelope = AsbTransportTestData.CreateEnvelope();
    var asReceived = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = JsonElement.Parse("{\"content\":\"raw\"}"),
      Hops = envelope.Hops,
      DispatchContext = envelope.DispatchContext,
    };
    return JsonSerializer.Serialize(asReceived,
      AsbTransportTestData.CombinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
  }

  private sealed class FixedInterceptor(InterceptorResult result) : IReceptorInterceptor {
    public List<Type> ReceptorTypes { get; } = [];

    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) {
      ReceptorTypes.Add(receptorType);
      return new ValueTask<InterceptorResult>(result);
    }
  }
}
