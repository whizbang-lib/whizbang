using System.Runtime.CompilerServices;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>Thread-safe record of what the rebuilder published, in the order it published it.</summary>
internal sealed class PublishedEventLog {
  private readonly List<object> _events = [];
  private readonly object _gate = new();

  public void Add(object e) { lock (_gate) { _events.Add(e); } }

  public IReadOnlyList<T> OfKind<T>() { lock (_gate) { return [.. _events.OfType<T>()]; } }

  public int Count { get { lock (_gate) { return _events.Count; } } }
}

/// <summary>
/// IDispatcher double that keeps what was published, modeled on the Sagas tests' RecordingDispatcher. Only the
/// PublishAsync overloads and PublishOnceAsync do anything; every other member throws, so a rebuild that starts
/// routing through a different surface fails loudly instead of passing quietly.
/// </summary>
internal sealed class RecordingRebuildDispatcher(PublishedEventLog log) : IDispatcher {
  public int SimpleCallCount { get; private set; }
  public int OptionsCallCount { get; private set; }
  public int PublishOnceCallCount { get; private set; }
  public DispatchOptions? LastCapturedOptions { get; private set; }
  public string? LastPublishOnceClaimKey { get; private set; }
  public Whizbang.Core.Lenses.PerspectiveScope? LastPublishOnceScope { get; private set; }
  public List<(string ClaimKey, object? Event, Whizbang.Core.Lenses.PerspectiveScope? Scope)> PublishOnceCalls { get; } = [];

  private static DeliveryReceipt _noopReceipt() =>
    DeliveryReceipt.Accepted(new MessageId(Guid.NewGuid()), destination: "test");

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    SimpleCallCount++;
    log.Add(eventData!);
    return Task.FromResult<IDeliveryReceipt>(_noopReceipt());
  }

  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
    OptionsCallCount++;
    LastCapturedOptions = options;
    log.Add(eventData!);
    return Task.FromResult<IDeliveryReceipt>(_noopReceipt());
  }

  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) {
    PublishOnceCallCount++;
    LastPublishOnceClaimKey = claimKey;
    LastPublishOnceScope = Whizbang.Core.Security.ScopeContextAccessor.CurrentContext?.Scope;
    PublishOnceCalls.Add((claimKey, eventData, LastPublishOnceScope));
    return Task.FromResult(true);
  }

  // Everything else — throws if exercised.
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => _ns();
  public Task<IDeliveryReceipt> SendAsync(object message) => _ns();
  public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _ns();
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => _ns();
  public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => _ns();
  public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _ns();
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => _nsVt<TResult>();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => _nsVt<TResult>();
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVt<TResult>();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVt<TResult>();
  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => _nsVtVoid();
  public ValueTask LocalInvokeAsync(object message) => _nsVtVoid();
  public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVtVoid();
  public ValueTask LocalInvokeAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVtVoid();
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => _nsVt<TResult>();
  public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => _nsVtVoid();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => _nsVt<InvokeResult<TResult>>();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => _nsVt<InvokeResult<TResult>>();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVt<InvokeResult<TResult>>();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVt<InvokeResult<TResult>>();
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => _nsVt<InvokeResult<TResult>>();
  public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => _ns<IEnumerable<IDeliveryReceipt>>();
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => _ns<IEnumerable<IDeliveryReceipt>>();
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => _nsVt<IEnumerable<IDeliveryReceipt>>();
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => _nsVt<IEnumerable<IDeliveryReceipt>>();
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => _ns<IEnumerable<IDeliveryReceipt>>();
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => _ns<IEnumerable<IDeliveryReceipt>>();
  public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => _nsVt<IEnumerable<TResult>>();

  private static Task<IDeliveryReceipt> _ns() => throw new NotSupportedException("Method not exercised by these tests.");
  private static Task<T> _ns<T>() => throw new NotSupportedException("Method not exercised by these tests.");
  private static ValueTask<T> _nsVt<T>() => throw new NotSupportedException("Method not exercised by these tests.");
  private static ValueTask _nsVtVoid() => throw new NotSupportedException("Method not exercised by these tests.");
}
