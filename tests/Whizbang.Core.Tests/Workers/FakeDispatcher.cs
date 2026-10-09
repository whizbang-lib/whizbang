// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

// Dispatcher and envelope doubles shared by the unit and component projects (one source, compiled into both).

internal class FakeDispatcher : IDispatcher {
  public int DispatchCallCount { get; private set; }

  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public Task<IDeliveryReceipt> SendAsync(object message) {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, Whizbang.Core.Dispatch.DispatchOptions options) where TMessage : notnull {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public Task<IDeliveryReceipt> SendAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    Whizbang.Core.Dispatch.DispatchOptions options,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) {
    DispatchCallCount++;
    return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
  }

  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync(object message) =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0
  ) =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) =>
    throw new NotImplementedException();

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) =>
    throw new NotImplementedException();

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, Whizbang.Core.Dispatch.DispatchOptions options) =>
    throw new NotImplementedException();

  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) =>
    throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
    throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
    throw new NotImplementedException();

  public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
    throw new NotImplementedException();

  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) =>
    throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull =>
    throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) =>
    throw new NotImplementedException();

  public static Task CascadeMessageAsync(IMessage message, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotImplementedException();
  public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotImplementedException();
  public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotImplementedException();
  public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
  public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotImplementedException();
}

internal sealed class FakeDeliveryReceipt : IDeliveryReceipt {
  public MessageId MessageId => MessageId.New();
  public CorrelationId? CorrelationId => null;
  public MessageId? CausationId => null;
  public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
  public string Destination => "test-destination";
  public DeliveryStatus Status => DeliveryStatus.Delivered;
  public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
  public Guid? StreamId => null;
}

internal sealed class FakeMessageEnvelope : IMessageEnvelope {

  public FakeMessageEnvelope(MessageId messageId, CorrelationId? correlationId) {
    MessageId = messageId;
    // Add at least one hop (required by interface)
    Hops.Add(new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "test-service",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 1234
      },
      CorrelationId = correlationId
    });
  }

  public int Version => 1;
  public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox };
  public MessageId MessageId { get; }
  public object Payload => new { };
  public List<MessageHop> Hops { get; } = [];

  public void AddHop(MessageHop hop) => Hops.Add(hop);
  public DateTimeOffset GetMessageTimestamp() => Hops[0].Timestamp;
  public CorrelationId? GetCorrelationId() => Hops[0].CorrelationId;
  public MessageId? GetCausationId() => Hops[0].CausationId;
  public JsonElement? GetMetadata(string key) => null;
  public SecurityContext? GetCurrentSecurityContext() => null;
  public ScopeContext? GetCurrentScope() => null;
}
