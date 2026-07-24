using RabbitMQ.Client;
using RabbitMQ.Client.Events;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)
#pragma warning disable CA1852 // Type can be sealed (test doubles)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Test doubles for RabbitMQ interfaces.
/// Simple manual mocks since Rocks isn't working yet.
/// Only implements members actually used by RabbitMQChannelPool tests.
/// </summary>
internal class FakeConnection(Func<Task<IChannel>> channelFactory, bool isOpen = true) : IConnection {
  private readonly Func<Task<IChannel>> _channelFactory = channelFactory;
  private readonly bool _isOpen = isOpen;

  public Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default) {
    return _channelFactory();
  }

  // Required interface members (not used in tests) - minimal implementation
  public ushort ChannelMax => 0;
  public IDictionary<string, object?> ClientProperties => new Dictionary<string, object?>();
  public ShutdownEventArgs? CloseReason => null;
  public AmqpTcpEndpoint Endpoint => throw new NotImplementedException();
  public uint FrameMax => 0;
  public TimeSpan Heartbeat => TimeSpan.Zero;
  public bool IsOpen => _isOpen;
  public AmqpTcpEndpoint[] KnownHosts => [];
  public IProtocol Protocol => throw new NotImplementedException();
  public IDictionary<string, object?>? ServerProperties => null;
  public IEnumerable<ShutdownReportEntry> ShutdownReport => [];
  public string? ClientProvidedName => null;
  public int LocalPort => 0;
  public int RemotePort => 0;

  public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
  public event AsyncEventHandler<ConnectionBlockedEventArgs>? ConnectionBlockedAsync;
  public event AsyncEventHandler<ShutdownEventArgs>? ConnectionShutdownAsync;
  public event AsyncEventHandler<AsyncEventArgs>? ConnectionUnblockedAsync;
  public event AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>? ConsumerTagChangeAfterRecoveryAsync;
  public event AsyncEventHandler<QueueNameChangedAfterRecoveryEventArgs>? QueueNameChangedAfterRecoveryAsync;
  private AsyncEventHandler<AsyncEventArgs>? _recoverySucceededAsync;
  public event AsyncEventHandler<AsyncEventArgs>? RecoverySucceededAsync {
    add => _recoverySucceededAsync += value;
    remove => _recoverySucceededAsync -= value;
  }

  /// <summary>
  /// Simulates connection recovery for testing.
  /// </summary>
  public async Task SimulateRecoverySucceededAsync() {
    if (_recoverySucceededAsync != null) {
      await _recoverySucceededAsync.Invoke(this, new AsyncEventArgs());
    }
  }
  public event AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryErrorAsync;
  public event AsyncEventHandler<RecoveringConsumerEventArgs>? RecoveringConsumerAsync;

  public Task CloseAsync(ushort reasonCode, string reasonText, TimeSpan timeout, bool abort, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  public void Dispose() { }
  public Task UpdateSecretAsync(string newSecret, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal class FakeChannel : IChannel {
  public bool IsDisposed { get; private set; }

  // Track method calls for PublishAsync tests
  public bool ExchangeDeclareAsyncCalled { get; private set; }
  public bool BasicPublishAsyncCalled { get; private set; }

  // Track exchange declarations with parameters for provisioning tests
  public List<(string Exchange, string Type, bool Durable, bool AutoDelete)> DeclaredExchanges { get; } = [];

  // Track method calls for SubscribeAsync tests
  public bool QueueDeclareAsyncCalled { get; private set; }
  public bool QueueBindAsyncCalled { get; private set; }
  public bool BasicConsumeAsyncCalled { get; private set; }
  public bool BasicCancelAsyncCalled { get; private set; }
  public string? LastConsumerTag { get; private set; }
  public string? LastDeclaredQueueName { get; private set; }

  // Members actually used by RabbitMQChannelPool
  public bool IsOpen => !IsDisposed;
  public void Dispose() => IsDisposed = true;
  public ValueTask DisposeAsync() {
    IsDisposed = true;
    return ValueTask.CompletedTask;
  }

  // Required interface members (not used in pooling tests) - minimal implementation
  public int ChannelNumber => 1;
  public ShutdownEventArgs? CloseReason => null;
  public IAsyncBasicConsumer? DefaultConsumer { get; set; }
  public bool IsClosed => IsDisposed;
  public ulong NextPublishSeqNo => 0;
  public string? CurrentQueue => null;
  public TimeSpan ContinuationTimeout { get; set; } = TimeSpan.FromSeconds(10);

  // Events - use Async suffix for RabbitMQ 7.0
  public event AsyncEventHandler<BasicAckEventArgs>? BasicAcksAsync;
  public event AsyncEventHandler<BasicNackEventArgs>? BasicNacksAsync;
  public event AsyncEventHandler<BasicReturnEventArgs>? BasicReturnAsync;
  public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
  public event AsyncEventHandler<FlowControlEventArgs>? FlowControlAsync;

  private AsyncEventHandler<ShutdownEventArgs>? _channelShutdownAsync;
  public event AsyncEventHandler<ShutdownEventArgs>? ChannelShutdownAsync {
    add => _channelShutdownAsync += value;
    remove => _channelShutdownAsync -= value;
  }

  /// <summary>
  /// Simulates channel shutdown for testing.
  /// </summary>
  public async Task SimulateShutdownAsync(ShutdownInitiator initiator, string reason, Exception? exception = null) {
    ShutdownEventArgs args;
    if (exception != null) {
      args = new ShutdownEventArgs(initiator, 320, reason, exception: exception);
    } else {
      args = new ShutdownEventArgs(initiator, 320, reason);
    }
    if (_channelShutdownAsync != null) {
      await _channelShutdownAsync.Invoke(this, args);
    }
  }

  // Implement methods used by PublishAsync
  public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object?>? arguments, bool passive, bool noWait, CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ExchangeDeclareAsyncCalled = true;
    DeclaredExchanges.Add((exchange, type, durable, autoDelete));
    return Task.CompletedTask;
  }

  public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body = default, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader {
    if (ExceptionToThrowOnPublish != null) {
      throw ExceptionToThrowOnPublish;
    }
    BasicPublishAsyncCalled = true;
    PublishedMessages.Add((exchange, routingKey, body));
    LastPublishedProperties = basicProperties;
    return ValueTask.CompletedTask;
  }

  public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body = default, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader {
    if (ExceptionToThrowOnPublish != null) {
      throw ExceptionToThrowOnPublish;
    }
    BasicPublishAsyncCalled = true;
    return ValueTask.CompletedTask;
  }

  // Implement subscription methods for SubscribeAsync tests
  public Task<QueueDeclareOk> QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? arguments, bool passive, bool noWait, CancellationToken cancellationToken = default) {
    QueueDeclareAsyncCalled = true;
    LastDeclaredQueueName = queue;
    LastQueueDeclareArguments = arguments;
    // Return a fake QueueDeclareOk
    return Task.FromResult(new QueueDeclareOk(queue, 0, 0));
  }

  public virtual Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) {
    QueueBindAsyncCalled = true;
    QueueBindings.Add((queue, exchange, routingKey));
    return Task.CompletedTask;
  }

  public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object?>? arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default) {
    BasicConsumeAsyncCalled = true;
    LastConsumerTag = consumerTag;
    LastRegisteredConsumer = consumer;
    return Task.FromResult(consumerTag);
  }

  public Task BasicCancelAsync(string consumerTag, bool noWait, CancellationToken cancellationToken = default) {
    BasicCancelAsyncCalled = true;
    return Task.CompletedTask;
  }

  // Track ack/nack calls for message processing tests
  public bool BasicAckAsyncCalled { get; private set; }
  public ulong LastAckedDeliveryTag { get; private set; }
  public bool BasicNackAsyncCalled { get; private set; }
  public ulong LastNackedDeliveryTag { get; private set; }
  public bool LastNackRequeue { get; private set; }
  public IAsyncBasicConsumer? LastRegisteredConsumer { get; private set; }

  // Track published messages for verification
  public List<(string Exchange, string RoutingKey, ReadOnlyMemory<byte> Body)> PublishedMessages { get; } = [];
  public IReadOnlyBasicProperties? LastPublishedProperties { get; private set; }

  // Track queue bindings
  public List<(string Queue, string Exchange, string RoutingKey)> QueueBindings { get; } = [];

  // Track queue arguments for dead letter exchange verification
  public IDictionary<string, object?>? LastQueueDeclareArguments { get; private set; }

  // Optional: throw specific exceptions from methods
  public Exception? ExceptionToThrowOnPublish { get; set; }
  public Exception? ExceptionToThrowOnAck { get; set; }
  public Exception? ExceptionToThrowOnNack { get; set; }

  // All other methods throw NotImplementedException
  public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task AbortAsync(ushort replyCode, string replyText, CancellationToken cancellationToken = default) => throw new NotImplementedException();

  public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) {
    if (ExceptionToThrowOnAck != null) {
      throw ExceptionToThrowOnAck;
    }
    BasicAckAsyncCalled = true;
    LastAckedDeliveryTag = deliveryTag;
    return ValueTask.CompletedTask;
  }

  public Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) => throw new NotImplementedException();

  public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
    if (ExceptionToThrowOnNack != null) {
      throw ExceptionToThrowOnNack;
    }
    BasicNackAsyncCalled = true;
    LastNackedDeliveryTag = deliveryTag;
    LastNackRequeue = requeue;
    return ValueTask.CompletedTask;
  }

  // Implement BasicQosAsync for SubscribeAsync tests
  public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ShutdownEventArgs reason, bool abort) => Task.CompletedTask;
  public ValueTask ConfirmSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeBindAsync(string destination, string source, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeDeleteAsync(string exchange, bool ifUnused, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeUnbindAsync(string destination, string source, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task QueueUnbindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxCommitAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxRollbackAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<bool> WaitForConfirmsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task WaitForConfirmsOrDieAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
