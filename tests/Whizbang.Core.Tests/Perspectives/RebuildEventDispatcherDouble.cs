using System.Runtime.CompilerServices;
using System.Text.Json;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// IDispatcher double whose publish behaviour is the subject: it records what was published, or throws.
/// </summary>
/// <remarks>
/// The rebuilder publishes its lifecycle events best-effort — a rebuild that did its work must not be reported
/// as failed because the record of it could not be written. Proving that needs a dispatcher that fails on
/// demand, which is what <paramref name="throwOnPublish"/> is for. Every other member throws, so a rebuild that
/// starts routing through a different surface fails loudly rather than passing quietly.
/// </remarks>
internal sealed class RebuildEventDispatcherDouble(bool throwOnPublish = false) : IDispatcher {

  /// <summary>What was published, in order.</summary>
  public List<object> Published { get; } = [];

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    if (throwOnPublish) { throw new InvalidOperationException("publish refused by the test double"); }
    Published.Add(eventData!);
    return Task.FromResult(_emptyReceipt);
  }

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) =>
    PublishAsync(eventData);

  private static readonly IDeliveryReceipt _emptyReceipt = new MockDeliveryReceipt();

  // SendAsync overloads
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
      Task.FromResult(_emptyReceipt);

  public Task<IDeliveryReceipt> SendAsync(object message) =>
      Task.FromResult(_emptyReceipt);

  public Task<IDeliveryReceipt> SendAsync(
      object message,
      IMessageContext context,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) =>
      Task.FromResult(_emptyReceipt);

  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull =>
      Task.FromResult(_emptyReceipt);

  public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) =>
      Task.FromResult(_emptyReceipt);

  public Task<IDeliveryReceipt> SendAsync(
      object message,
      IMessageContext context,
      DispatchOptions options,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) =>
      Task.FromResult(_emptyReceipt);

  // LocalInvokeAsync overloads
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
      ValueTask.FromResult(default(TResult)!);

  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
      ValueTask.FromResult(default(TResult)!);

  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
      TMessage message,
      IMessageContext context,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) where TMessage : notnull =>
      ValueTask.FromResult(default(TResult)!);

  public ValueTask<TResult> LocalInvokeAsync<TResult>(
      object message,
      IMessageContext context,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) =>
      ValueTask.FromResult(default(TResult)!);

  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
      ValueTask.CompletedTask;

  public ValueTask LocalInvokeAsync(object message) =>
      ValueTask.CompletedTask;

  public ValueTask LocalInvokeAsync<TMessage>(
      TMessage message,
      IMessageContext context,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) where TMessage : notnull =>
      ValueTask.CompletedTask;

  public ValueTask LocalInvokeAsync(
      object message,
      IMessageContext context,
      string callerMemberName = "",
      string callerFilePath = "",
      int callerLineNumber = 0) =>
      ValueTask.CompletedTask;

  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) =>
      ValueTask.FromResult(default(TResult)!);

  public ValueTask LocalInvokeAsync(object message, DispatchOptions options) =>
      ValueTask.CompletedTask;

  // PublishAsync overloads


  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

  // CascadeMessageAsync
  public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

  // Batch operations
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
      Task.FromResult(Enumerable.Empty<IDeliveryReceipt>());

  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
      Task.FromResult(Enumerable.Empty<IDeliveryReceipt>());

  public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
      ValueTask.FromResult(Enumerable.Empty<TResult>());

  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
      throw new NotImplementedException();

  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) =>
      throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull =>
      throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) =>
      throw new NotImplementedException();

  // LocalInvokeWithReceiptAsync overloads
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotImplementedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotImplementedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotImplementedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotImplementedException();

  /// <summary>
  /// Minimal mock delivery receipt for testing.
  /// </summary>
  private sealed class MockDeliveryReceipt : IDeliveryReceipt {
    public MessageId MessageId { get; } = MessageId.New();
    public CorrelationId? CorrelationId => null;
    public MessageId? CausationId => null;
    public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
    public string Destination => "MockDestination";
    public DeliveryStatus Status => DeliveryStatus.Accepted;
    public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
    public Guid? StreamId => null;
  }
}
