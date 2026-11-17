// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The wire pieces two real Postgres stores and the real integrity receptors talk through in a test: a transport that
/// captures what each side publishes, a dispatcher that captures the events a side raises (divergence reports among
/// them), and the identity a side reports. <see cref="TwoServiceHarness"/> drives the integrity protocol over it, and
/// <see cref="ReconcileConvergenceE2ETests"/> and <see cref="CleanupIntegrityMatrixTests"/> share the same wire.
/// </summary>
internal sealed class IntegrityWireTransport : ITransport {
  /// <summary>Everything this side published, in order.</summary>
  public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
  public bool IsInitialized => true;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
  public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
    lock (Published) {
      Published.Add((envelope, destination, envelopeType));
    }
    return Task.CompletedTask;
  }
  public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
}

/// <summary>Captures the events a side publishes through its dispatcher (divergence reports, among others).</summary>
internal sealed class IntegrityWireDispatcher : IDispatcher {
  /// <summary>Everything published, in order.</summary>
  public List<object> Published { get; } = [];

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    lock (Published) {
      Published.Add(eventData!);
    }
    return Task.FromResult<IDeliveryReceipt>(new Receipt());
  }

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => PublishAsync(eventData);
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
  public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
  public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
  public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => throw new NotSupportedException();
  public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
  public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
  public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
  public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

  private sealed class Receipt : IDeliveryReceipt {
    public MessageId MessageId => Whizbang.Core.ValueObjects.MessageId.New();
    public CorrelationId? CorrelationId => null;
    public MessageId? CausationId => null;
    public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch;
    public string Destination => "test";
    public DeliveryStatus Status => DeliveryStatus.Delivered;
    public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
    public Guid? StreamId => null;
  }
}

/// <summary>The identity a side reports: its service name, on a fixed instance.</summary>
internal sealed class IntegrityWireInstance(string name) : IServiceInstanceProvider {
  public Guid InstanceId { get; } = TrackedGuid.New();
  public string ServiceName => name;
  public string HostName => "e2e-host";
  public int ProcessId => 1;
  public ServiceInstanceInfo ToInfo() => new() {
    InstanceId = InstanceId,
    ServiceName = name,
    HostName = HostName,
    ProcessId = ProcessId,
  };
}
