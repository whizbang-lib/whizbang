using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Covers the branches of <see cref="ServiceCollectionExtensions"/> not exercised by
/// <c>ServiceCollectionExtensionsTests</c>: the SharedTopicOutboxStrategy inbox-topic branch
/// of the IMessagePublishStrategy factory, the connection state monitoring wire-up
/// (including every event handler body), and AddRabbitMQHealthChecks.
/// </summary>
public class ServiceCollectionExtensionsBranchCoverageTests {
  private const string CONNECTION_STRING = "amqp://guest:guest@localhost:5672/";

  // --- IMessagePublishStrategy inbox-topic branch selection ---

  [Test]
  public async Task AddRabbitMQTransport_WithSharedTopicOutboxStrategy_UsesConfiguredInboxTopicAsync() {
    // Arrange
    var services = _createServicesWithFakeConnection();
    services.AddSingleton<IOutboxRoutingStrategy>(
      new SharedTopicOutboxStrategy("custom-inbox", PassthroughRoutingStrategy.Instance));

    // Act
    services.AddRabbitMQTransport(CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    // Assert - the SharedTopicOutboxStrategy branch must propagate the configured inbox topic
    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo("custom-inbox");
  }

  [Test]
  public async Task AddRabbitMQTransport_WithoutOutboxRoutingStrategy_UsesDefaultInboxTopicAsync() {
    // Arrange
    var services = _createServicesWithFakeConnection();

    // Act
    services.AddRabbitMQTransport(CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    // Assert - no IOutboxRoutingStrategy registered means the default inbox topic is used
    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo(SharedTopicOutboxStrategy.DefaultInboxTopic);
  }

  [Test]
  public async Task AddRabbitMQTransport_WithNamespaceOutboxStrategy_WiresPublishTimeFlipSeamAsync() {
    // Phase 6: the DI factory must recognize NamespaceOutboxStrategy, propagate its shared
    // inbox topic, AND hand the strategy itself to TransportPublishStrategy so the
    // publish-time command resolution consults the flip set.
    var services = _createServicesWithFakeConnection();
    var namespaceStrategy = new NamespaceOutboxStrategy(new RoutingOptions(), "custom-inbox");
    services.AddSingleton<IOutboxRoutingStrategy>(namespaceStrategy);

    services.AddRabbitMQTransport(CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo("custom-inbox");
    await Assert.That(_getNamespaceRouting(strategy)).IsSameReferenceAs(namespaceStrategy)
      .Because("without the seam, flips would silently never reach the wire");
  }

  [Test]
  public async Task AddRabbitMQTransport_WithSharedTopicOutboxStrategy_ResolverSeamWiredAndNeverFlipsAsync() {
    // Phase 7 seam unification: the DI factory consumes ICommandInboxAddressResolver — no
    // concrete-strategy type tests. The shared-topic strategy rides the SAME wiring as the
    // namespace strategy; byte-identical behavior holds because its resolver never flips.
    var services = _createServicesWithFakeConnection();
    var sharedStrategy = new SharedTopicOutboxStrategy("custom-inbox", PassthroughRoutingStrategy.Instance);
    services.AddSingleton<IOutboxRoutingStrategy>(sharedStrategy);

    services.AddRabbitMQTransport(CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    var seam = _getNamespaceRouting(strategy);
    await Assert.That(seam).IsSameReferenceAs(sharedStrategy)
      .Because("one interface seam serves every command-routing strategy — no type tests in the factory");
    await Assert.That(seam!.ResolveFlippedCommandInboxAddress("myapp.orders.commands")).IsNull()
      .Because("the shared strategy never flips, so the wiring stays byte-identical to phase 6");
  }

  [Test]
  public async Task AddRabbitMQTransport_WithNonSharedTopicOutboxStrategy_FallsBackToDefaultInboxTopicAsync() {
    // Arrange - a registered strategy OUTSIDE the command-inbox seam falls back to defaults
    var services = _createServicesWithFakeConnection();
    services.AddSingleton<IOutboxRoutingStrategy>(
      new DomainTopicOutboxStrategy(PassthroughRoutingStrategy.Instance));

    // Act
    services.AddRabbitMQTransport(CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    // Assert
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo(SharedTopicOutboxStrategy.DefaultInboxTopic);
    await Assert.That(_getNamespaceRouting(strategy)).IsNull()
      .Because("a strategy outside the seam wires no flip hook — commands ride the default inbox topic");
  }

  // --- _wireUpConnectionStateMonitoring ---

  [Test]
  public async Task WireUpConnectionStateMonitoring_WithNullLogger_AttachesNoHandlersAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();

    // Act - null logger takes the early-return branch
    _invokeWireUpConnectionStateMonitoring(connection, logger: null);

    // Assert
    await Assert.That(connection.HasAnyMonitoringHandlers).IsFalse();
  }

  [Test]
  public async Task WireUpConnectionStateMonitoring_ConnectionShutdown_LogsWarningWithReplyDetailsAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();
    var logger = new CapturingLogger();
    _invokeWireUpConnectionStateMonitoring(connection, logger);
    await Assert.That(connection.HasAnyMonitoringHandlers).IsTrue();

    // Act
    await connection.RaiseConnectionShutdownAsync(
      new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "connection-forced"));

    // Assert
    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.Entries[0].Message).Contains("RabbitMQ connection shutdown");
    await Assert.That(logger.Entries[0].Message).Contains("320");
    await Assert.That(logger.Entries[0].Message).Contains("connection-forced");
  }

  [Test]
  public async Task WireUpConnectionStateMonitoring_RecoverySucceeded_LogsInformationAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();
    var logger = new CapturingLogger();
    _invokeWireUpConnectionStateMonitoring(connection, logger);

    // Act
    await connection.RaiseRecoverySucceededAsync(new AsyncEventArgs());

    // Assert
    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(logger.Entries[0].Message).Contains("recovered successfully");
  }

  [Test]
  public async Task WireUpConnectionStateMonitoring_RecoveryError_LogsErrorWithExceptionAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();
    var logger = new CapturingLogger();
    _invokeWireUpConnectionStateMonitoring(connection, logger);
    var recoveryException = new InvalidOperationException("recovery boom");

    // Act
    await connection.RaiseConnectionRecoveryErrorAsync(
      new ConnectionRecoveryErrorEventArgs(recoveryException));

    // Assert
    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(logger.Entries[0].Message).Contains("recovery attempt failed");
    await Assert.That(ReferenceEquals(logger.Entries[0].Exception, recoveryException)).IsTrue();
  }

  [Test]
  public async Task WireUpConnectionStateMonitoring_ConnectionBlocked_LogsWarningWithReasonAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();
    var logger = new CapturingLogger();
    _invokeWireUpConnectionStateMonitoring(connection, logger);

    // Act
    await connection.RaiseConnectionBlockedAsync(new ConnectionBlockedEventArgs("memory alarm"));

    // Assert
    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.Entries[0].Message).Contains("blocked by broker");
    await Assert.That(logger.Entries[0].Message).Contains("memory alarm");
  }

  [Test]
  public async Task WireUpConnectionStateMonitoring_ConnectionUnblocked_LogsInformationAsync() {
    // Arrange
    var connection = new MonitorableFakeConnection();
    var logger = new CapturingLogger();
    _invokeWireUpConnectionStateMonitoring(connection, logger);

    // Act
    await connection.RaiseConnectionUnblockedAsync(new AsyncEventArgs());

    // Assert
    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(logger.Entries[0].Message).Contains("unblocked");
  }

  // --- AddRabbitMQHealthChecks ---

  [Test]
  public async Task AddRabbitMQHealthChecks_RegistersRabbitMQCheck_WithUnhealthyFailureStatusAsync() {
    // Arrange
    var services = _createServicesWithFakeConnection();
    services.AddRabbitMQTransport(CONNECTION_STRING);

    // Act
    services.AddRabbitMQHealthChecks();
    var provider = services.BuildServiceProvider();
    var healthOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

    // Assert - the "rabbitmq" registration must exist with Unhealthy failure status
    var registration = healthOptions.Registrations.Single(r => r.Name == "rabbitmq");
    await Assert.That(registration.FailureStatus).IsEqualTo(HealthStatus.Unhealthy);

    // Assert - the factory lambda produces a RabbitMQHealthCheck from registered services
    var healthCheck = registration.Factory(provider);
    await Assert.That(healthCheck).IsTypeOf<RabbitMQHealthCheck>();
  }

  [Test]
  public async Task AddRabbitMQHealthChecks_ReturnsSameServiceCollection_ForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddRabbitMQHealthChecks();

    // Assert
    await Assert.That(ReferenceEquals(result, services)).IsTrue();
  }

  // --- helpers ---

  private static ServiceCollection _createServicesWithFakeConnection() {
    var services = new ServiceCollection();
    services.AddSingleton<IConnection>(_ => new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddLogging();
    return services;
  }

  /// <summary>
  /// Invokes the private static _wireUpConnectionStateMonitoring method.
  /// The production call site only runs inside the IConnection factory after a real broker
  /// connection succeeds, so unit tests reach it via reflection with a fake connection.
  /// </summary>
  private static void _invokeWireUpConnectionStateMonitoring(IConnection connection, ILogger? logger) {
    var method = typeof(ServiceCollectionExtensions).GetMethod(
      "_wireUpConnectionStateMonitoring",
      BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException(
        "_wireUpConnectionStateMonitoring not found on ServiceCollectionExtensions - was it renamed?");

    method.Invoke(null, [connection, logger]);
  }

  /// <summary>
  /// Reads the private inbox topic captured by TransportPublishStrategy so tests can assert
  /// which branch of the IMessagePublishStrategy factory selected the topic.
  /// </summary>
  private static string? _getInboxTopic(IMessagePublishStrategy strategy) {
    var field = typeof(TransportPublishStrategy).GetField(
      "_inboxTopic",
      BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException(
        "_inboxTopic field not found on TransportPublishStrategy - was it renamed?");

    return (string?)field.GetValue(strategy);
  }

  /// <summary>
  /// Reads the private publish-time flip seam captured by TransportPublishStrategy so tests
  /// can assert the NamespaceOutboxStrategy branch handed the strategy through.
  /// </summary>
  private static ICommandInboxAddressResolver? _getNamespaceRouting(IMessagePublishStrategy strategy) {
    var field = typeof(TransportPublishStrategy).GetField(
      "_namespaceRouting",
      BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException(
        "_namespaceRouting field not found on TransportPublishStrategy - was it renamed?");

    return (ICommandInboxAddressResolver?)field.GetValue(strategy);
  }

  /// <summary>
  /// Capturing logger that records every log entry for assertion.
  /// </summary>
  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception), exception));
    }
  }

  /// <summary>
  /// IConnection fake whose monitored events can be raised from tests.
  /// The shared FakeConnection in TestDoubles.cs uses auto-implemented events that cannot be
  /// fired externally, so this fake backs each monitored event with an invokable field.
  /// </summary>
  private sealed class MonitorableFakeConnection : IConnection {
    private AsyncEventHandler<ShutdownEventArgs>? _connectionShutdownAsync;
    private AsyncEventHandler<AsyncEventArgs>? _recoverySucceededAsync;
    private AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? _connectionRecoveryErrorAsync;
    private AsyncEventHandler<ConnectionBlockedEventArgs>? _connectionBlockedAsync;
    private AsyncEventHandler<AsyncEventArgs>? _connectionUnblockedAsync;

    public bool HasAnyMonitoringHandlers =>
      _connectionShutdownAsync != null ||
      _recoverySucceededAsync != null ||
      _connectionRecoveryErrorAsync != null ||
      _connectionBlockedAsync != null ||
      _connectionUnblockedAsync != null;

    public event AsyncEventHandler<ShutdownEventArgs>? ConnectionShutdownAsync {
      add => _connectionShutdownAsync += value;
      remove => _connectionShutdownAsync -= value;
    }

    public event AsyncEventHandler<AsyncEventArgs>? RecoverySucceededAsync {
      add => _recoverySucceededAsync += value;
      remove => _recoverySucceededAsync -= value;
    }

    public event AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryErrorAsync {
      add => _connectionRecoveryErrorAsync += value;
      remove => _connectionRecoveryErrorAsync -= value;
    }

    public event AsyncEventHandler<ConnectionBlockedEventArgs>? ConnectionBlockedAsync {
      add => _connectionBlockedAsync += value;
      remove => _connectionBlockedAsync -= value;
    }

    public event AsyncEventHandler<AsyncEventArgs>? ConnectionUnblockedAsync {
      add => _connectionUnblockedAsync += value;
      remove => _connectionUnblockedAsync -= value;
    }

    public Task RaiseConnectionShutdownAsync(ShutdownEventArgs args) =>
      _connectionShutdownAsync?.Invoke(this, args) ?? Task.CompletedTask;

    public Task RaiseRecoverySucceededAsync(AsyncEventArgs args) =>
      _recoverySucceededAsync?.Invoke(this, args) ?? Task.CompletedTask;

    public Task RaiseConnectionRecoveryErrorAsync(ConnectionRecoveryErrorEventArgs args) =>
      _connectionRecoveryErrorAsync?.Invoke(this, args) ?? Task.CompletedTask;

    public Task RaiseConnectionBlockedAsync(ConnectionBlockedEventArgs args) =>
      _connectionBlockedAsync?.Invoke(this, args) ?? Task.CompletedTask;

    public Task RaiseConnectionUnblockedAsync(AsyncEventArgs args) =>
      _connectionUnblockedAsync?.Invoke(this, args) ?? Task.CompletedTask;

    // Unmonitored events - never raised by these tests
#pragma warning disable CS0067 // Event is never used
    public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
    public event AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>? ConsumerTagChangeAfterRecoveryAsync;
    public event AsyncEventHandler<QueueNameChangedAfterRecoveryEventArgs>? QueueNameChangedAfterRecoveryAsync;
    public event AsyncEventHandler<RecoveringConsumerEventArgs>? RecoveringConsumerAsync;
#pragma warning restore CS0067

    // Minimal member implementations - not used by the monitoring wire-up
    public ushort ChannelMax => 0;
    public IDictionary<string, object?> ClientProperties => new Dictionary<string, object?>();
    public ShutdownEventArgs? CloseReason => null;
    public AmqpTcpEndpoint Endpoint => throw new NotImplementedException();
    public uint FrameMax => 0;
    public TimeSpan Heartbeat => TimeSpan.Zero;
    public bool IsOpen => true;
    public AmqpTcpEndpoint[] KnownHosts => [];
    public IProtocol Protocol => throw new NotImplementedException();
    public IDictionary<string, object?>? ServerProperties => null;
    public IEnumerable<ShutdownReportEntry> ShutdownReport => [];
    public string? ClientProvidedName => null;
    public int LocalPort => 0;
    public int RemotePort => 0;

    public Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default) =>
      Task.FromResult<IChannel>(new FakeChannel());

    public Task CloseAsync(ushort reasonCode, string reasonText, TimeSpan timeout, bool abort, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task CloseAsync(ShutdownEventArgs _, bool __, CancellationToken ___ = default) =>
      Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() { }

    public Task UpdateSecretAsync(string newSecret, string reason, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }
}
