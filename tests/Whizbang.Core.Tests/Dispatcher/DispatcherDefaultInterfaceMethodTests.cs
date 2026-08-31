using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Covers the default interface method bodies on <see cref="IDispatcher"/>.
/// The shipped Dispatcher overrides every one of these, so the inherited
/// fallbacks are only reachable through an implementation that leaves them
/// alone. Each is expected to refuse rather than silently no-op.
/// </summary>
[Category("Core")]
[Category("Dispatcher")]
public class DispatcherDefaultInterfaceMethodTests {

  private sealed record TestMessage(Guid Id);

  private sealed class TestPerspective;

  /// <summary>
  /// Implements only the abstract members of <see cref="IDispatcher"/> so the
  /// sync-related defaults stay inherited. Every abstract member throws
  /// <see cref="NotImplementedException"/> so that a test which accidentally
  /// reaches one fails loudly instead of passing for the wrong reason.
  /// </summary>
  private sealed class MinimalDispatcher : IDispatcher {
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> SendAsync(object message)
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options)
        => throw new NotImplementedException();

    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message)
        => throw new NotImplementedException();

    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask LocalInvokeAsync(object message)
        => throw new NotImplementedException();

    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options)
        => throw new NotImplementedException();

    public ValueTask LocalInvokeAsync(object message, DispatchOptions options)
        => throw new NotImplementedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message)
        where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message)
        => throw new NotImplementedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(
        TMessage message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options)
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData)
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options)
        => throw new NotImplementedException();

    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task CascadeMessageAsync(
        IMessage message,
        IMessageEnvelope? sourceEnvelope,
        DispatchModes mode,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull
        => throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages)
        => throw new NotImplementedException();

    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages)
        => throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull
        => throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events)
        => throw new NotImplementedException();

    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages)
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> SendAsync(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        => throw new NotImplementedException();

    public Task<IDeliveryReceipt> SendAsync(
        object message,
        IMessageContext context,
        DispatchOptions options,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        => throw new NotImplementedException();

    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
        TMessage message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask<TResult> LocalInvokeAsync<TResult>(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        => throw new NotImplementedException();

    public ValueTask LocalInvokeAsync<TMessage>(
        TMessage message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        where TMessage : notnull
        => throw new NotImplementedException();

    public ValueTask LocalInvokeAsync(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        => throw new NotImplementedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0)
        => throw new NotImplementedException();
  }

  // The three timeout-based overloads below are [Obsolete]; CS0618 is escalated to
  // an error repo-wide, so calling them from a test needs an explicit suppression.
#pragma warning disable CS0618

  [Test]
  public async Task LocalInvokeAndSyncAsync_TypedResult_WithoutAwaiterSupport_ThrowsAsync() {
    IDispatcher dispatcher = new MinimalDispatcher();
    var message = new TestMessage(Guid.CreateVersion7());

    await Assert.That(async () => {
      await dispatcher.LocalInvokeAndSyncAsync<TestMessage, int>(message);
    }).ThrowsExactly<NotSupportedException>();
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_SyncResult_WithoutAwaiterSupport_ThrowsAsync() {
    IDispatcher dispatcher = new MinimalDispatcher();
    var message = new TestMessage(Guid.CreateVersion7());

    await Assert.That(async () => {
      await dispatcher.LocalInvokeAndSyncAsync<TestMessage>(message);
    }).ThrowsExactly<NotSupportedException>();
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_SpecificPerspective_WithoutAwaiterSupport_ThrowsAsync() {
    IDispatcher dispatcher = new MinimalDispatcher();
    var message = new TestMessage(Guid.CreateVersion7());

    await Assert.That(async () => {
      await dispatcher.LocalInvokeAndSyncAsync<TestMessage, int, TestPerspective>(message);
    }).ThrowsExactly<NotSupportedException>();
  }

#pragma warning restore CS0618

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithoutAwaiterSupport_ThrowsAsync() {
    IDispatcher dispatcher = new MinimalDispatcher();
    var message = new TestMessage(Guid.CreateVersion7());

    await Assert.That(async () => {
      await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<TestMessage, TestPerspective>(message);
    }).ThrowsExactly<NotSupportedException>();
  }

  [Test]
  [Arguments(SyncMode.StreamOnly)]
  [Arguments(SyncMode.AllProjections)]
  public async Task LocalInvokeAndSyncAsync_SyncMode_WithoutEventStoreSupport_ThrowsAsync(SyncMode mode) {
    IDispatcher dispatcher = new MinimalDispatcher();
    var message = new TestMessage(Guid.CreateVersion7());

    await Assert.That(async () => {
      await dispatcher.LocalInvokeAndSyncAsync(message, mode);
    }).ThrowsExactly<NotSupportedException>();
  }
}
