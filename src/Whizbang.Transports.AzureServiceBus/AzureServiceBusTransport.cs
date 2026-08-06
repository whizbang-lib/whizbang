using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus implementation of ITransport.
/// Provides reliable, ordered message delivery using Azure Service Bus topics and subscriptions.
/// </summary>
/// <tests>No tests found</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Transport implementation with diagnostic logging - I/O bound operations where LoggerMessage overhead isn't justified")]
public class AzureServiceBusTransport : ITransport, ITransportWithRecovery, IAsyncDisposable {
  private const string ENVELOPE_TYPE_PROPERTY = "EnvelopeType";

  private readonly ServiceBusClient _client;
  private readonly IServiceBusAdminClient? _adminClient;
  private readonly ILogger<AzureServiceBusTransport> _logger;
  private readonly Dictionary<string, ServiceBusSender> _senders = [];
  private readonly SemaphoreSlim _senderLock = new(1, 1);
  private readonly AzureServiceBusOptions _options;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly AsbReceiveDecisionMaker _decisionMaker = new();
  private readonly Whizbang.Core.Messaging.IReceptorRegistry? _receptorRegistry;
  private readonly Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry? _perspectiveRegistry;
  private readonly Whizbang.Core.Messaging.IRawReceptorRegistry? _rawReceptorRegistry;
  private readonly Whizbang.Core.Messaging.IMessageTypeBinder _typeBinder;
  private readonly IMessageDiscardPolicy? _discardPolicy;
  private readonly IReadOnlySet<string> _absorbedNamespaces;
  private readonly bool _isEmulator;
  private Func<CancellationToken, Task>? _recoveryHandler;
  private bool _disposed;
  private bool _isInitialized;

  /// <summary>
  /// Initializes a new instance of AzureServiceBusTransport with a shared ServiceBusClient.
  /// The transport does NOT dispose the injected client - the DI container manages its lifetime.
  /// </summary>
  /// <param name="client">Shared ServiceBusClient instance (managed by DI container)</param>
  /// <param name="jsonOptions">JSON serialization options</param>
  /// <param name="options">Optional transport configuration</param>
  /// <param name="logger">Optional logger instance</param>
  /// <param name="adminClient">Optional admin client for auto-provisioning infrastructure</param>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Transport composes the ServiceBus client + admin client + multiple registries; each is independent and optional, bundling would obscure DI registration intent.")]
  public AzureServiceBusTransport(
    ServiceBusClient client,
    JsonSerializerOptions jsonOptions,
    AzureServiceBusOptions? options = null,
    ILogger<AzureServiceBusTransport>? logger = null,
    IServiceBusAdminClient? adminClient = null,
    Whizbang.Core.Messaging.IReceptorRegistry? receptorRegistry = null,
    Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry? perspectiveRegistry = null,
    Whizbang.Core.Messaging.IRawReceptorRegistry? rawReceptorRegistry = null,
    Whizbang.Core.Messaging.IMessageTypeBinder? typeBinder = null,
    IMessageDiscardPolicy? discardPolicy = null,
    IReadOnlySet<string>? absorbedNamespaces = null
  ) {
    using var activity = WhizbangActivitySource.Transport.StartActivity("AzureServiceBusTransport.Initialize");

    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    _client = client;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureServiceBusTransport>.Instance;
    _adminClient = adminClient;

    // Detect emulator from client endpoint
    var endpoint = client.FullyQualifiedNamespace;
    _isEmulator = endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                  endpoint.Contains("127.0.0.1");

    _jsonOptions = jsonOptions;
    _options = options ?? new AzureServiceBusOptions();
    _receptorRegistry = receptorRegistry;
    _perspectiveRegistry = perspectiveRegistry;
    _rawReceptorRegistry = rawReceptorRegistry;
    // Slice 4 — default to the multi-pass binder so the registry-miss → binder-cascade
    // fallback works without explicit DI configuration. Tests can inject a fake.
    _typeBinder = typeBinder ?? new Whizbang.Core.Messaging.MultiPassMessageTypeBinder();
    _discardPolicy = discardPolicy;
    _absorbedNamespaces = absorbedNamespaces
      ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Log admin client availability
    if (_adminClient != null) {
      _logger.LogInformation("Admin client provided - auto-provisioning enabled");
    } else {
      _logger.LogInformation("No admin client - auto-provisioning disabled, infrastructure must be pre-provisioned");
    }

    // Add OTEL tags for observability
    activity?.SetTag("transport.type", "AzureServiceBus");
    activity?.SetTag("transport.emulator", _isEmulator);
    activity?.SetTag("transport.admin_client_available", _adminClient != null);
    activity?.SetTag("transport.auto_provision", _options.AutoProvisionInfrastructure);
  }

  /// <inheritdoc />
  public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
    _recoveryHandler = onRecovered;
  }

  /// <summary>
  /// Slice 2 receptor-registry filter — returns true if any local receptor handles the given
  /// message type at any lifecycle stage, OR any local perspective Apply's it. Returns false
  /// (drop this message) when neither registry was injected — that signals a legacy / test
  /// configuration that should not exercise the filter.
  /// </summary>
  /// <remarks>
  /// Returns null when no registries are wired so the decision-maker treats the predicate as
  /// "filter disabled" and falls through to legacy behavior. Once registries are present, the
  /// closure is built once and reused across receives.
  /// </remarks>
  private Func<Type, bool>? _buildIsHandledLocally() {
    if (_receptorRegistry is null && _perspectiveRegistry is null) {
      return null;
    }

    return type => {
      if (_receptorRegistry != null) {
        foreach (var stage in Enum.GetValues<Whizbang.Core.Messaging.LifecycleStage>()) {
          if (_receptorRegistry.GetReceptorsFor(type, stage).Count > 0) {
            return true;
          }
        }
      }
      if (_perspectiveRegistry != null) {
        foreach (var et in _perspectiveRegistry.GetEventTypes()) {
          if (et == type) {
            return true;
          }
        }
      }
      return false;
    };
  }

  /// <summary>
  /// Determines if a Service Bus exception indicates a connection-level error
  /// that warrants triggering subscription recovery.
  /// </summary>
  private static bool _isConnectionError(Exception ex) {
    if (ex is ServiceBusException sbEx) {
      return sbEx.Reason is
        ServiceBusFailureReason.ServiceCommunicationProblem or
        ServiceBusFailureReason.ServiceBusy or
        ServiceBusFailureReason.ServiceTimeout;
    }
    return false;
  }

  /// <summary>
  /// Returns true when an exception thrown from
  /// <c>CompleteMessageAsync</c>/<c>AbandonMessageAsync</c>/<c>DeadLetterMessageAsync</c>
  /// represents a lost lock or shutdown — meaning the operation cannot succeed and the
  /// broker will redeliver the message naturally (or it has already been settled).
  /// Swallowing these in the consumer event handler prevents the unhandled exception from
  /// surfacing as noise on the processor's <c>ProcessErrorAsync</c> path.
  /// </summary>
  private static bool _isSettlementShouldSwallow(Exception ex) {
    if (ex is ObjectDisposedException) {
      return true;
    }
    if (ex is ServiceBusException sbEx) {
      return sbEx.Reason is
        ServiceBusFailureReason.MessageLockLost or
        ServiceBusFailureReason.SessionLockLost or
        ServiceBusFailureReason.MessageNotFound;
    }
    return false;
  }

  /// <summary>
  /// Wraps <see cref="ProcessMessageEventArgs.AbandonMessageAsync(ServiceBusReceivedMessage,
  /// IDictionary{string, object}, CancellationToken)"/> with a swallow on lock-lost / disposed
  /// shutdown — same rationale as the bulk-batch helpers and the RabbitMQ NACK helpers.
  /// </summary>
  private async Task _safeAbandonAsync(ProcessMessageEventArgs args) {
    try {
      await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
    } catch (Exception ex) when (_isSettlementShouldSwallow(ex)) {
      _logger.LogWarning(ex,
        "ASB Abandon swallowed for message {MessageId} — lock lost or processor disposed; broker will redeliver",
        args.Message.MessageId);
    }
  }

  /// <summary>
  /// Wraps <see cref="ProcessSessionMessageEventArgs.AbandonMessageAsync"/> with the same
  /// swallow policy as <see cref="_safeAbandonAsync(ProcessMessageEventArgs)"/>.
  /// </summary>
  private async Task _safeAbandonAsync(ProcessSessionMessageEventArgs args) {
    try {
      await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
    } catch (Exception ex) when (_isSettlementShouldSwallow(ex)) {
      _logger.LogWarning(ex,
        "ASB Abandon swallowed for session message {MessageId} (session {SessionId}) — lock lost or processor disposed; broker will redeliver",
        args.Message.MessageId, args.SessionId);
    }
  }

  /// <summary>
  /// Wraps <see cref="ProcessMessageEventArgs.DeadLetterMessageAsync"/> with the same
  /// swallow policy. If lock is lost the message returns to the queue for redelivery; on
  /// next attempt the same DLQ decision will be made.
  /// </summary>
  private async Task _safeDeadLetterAsync(ProcessMessageEventArgs args, string reason, string description) {
    try {
      await args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken: args.CancellationToken);
    } catch (Exception ex) when (_isSettlementShouldSwallow(ex)) {
      _logger.LogWarning(ex,
        "ASB DeadLetter swallowed for message {MessageId} — lock lost or processor disposed; broker will redeliver",
        args.Message.MessageId);
    }
  }

  /// <summary>
  /// Wraps <see cref="ProcessSessionMessageEventArgs.DeadLetterMessageAsync"/> with the
  /// same swallow policy.
  /// </summary>
  private async Task _safeDeadLetterAsync(ProcessSessionMessageEventArgs args, string reason, string description) {
    try {
      await args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken: args.CancellationToken);
    } catch (Exception ex) when (_isSettlementShouldSwallow(ex)) {
      _logger.LogWarning(ex,
        "ASB DeadLetter swallowed for session message {MessageId} (session {SessionId}) — lock lost or processor disposed; broker will redeliver",
        args.Message.MessageId, args.SessionId);
    }
  }

  /// <summary>
  /// Invokes the recovery handler if set and appropriate.
  /// </summary>
  private async Task _invokeRecoveryHandlerAsync() {
    if (_recoveryHandler != null) {
      try {
        _logger.LogInformation("Azure Service Bus connection recovered, invoking recovery handler");
        await _recoveryHandler(CancellationToken.None);
      } catch (Exception ex) {
        _logger.LogError(ex, "Error in recovery handler after Service Bus connection recovery");
      }
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public bool IsInitialized => _isInitialized;

  /// <inheritdoc />
  public ValueTask<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    => ValueTask.FromResult(!_client.IsClosed);

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    using var activity = WhizbangActivitySource.Transport.StartActivity("AzureServiceBusTransport.Initialize");

    cancellationToken.ThrowIfCancellationRequested();

    // Idempotent - only initialize once
    if (_isInitialized) {
      _logger.LogDebug("Transport already initialized, skipping");
      return;
    }

    try {
      // Verify client is not closed
      if (_client.IsClosed) {
        throw new InvalidOperationException("ServiceBusClient is closed and cannot be initialized");
      }

      // For emulator, we can't verify connectivity via admin API (not supported)
      // Just mark as initialized if client is not closed
      if (_isEmulator) {
        _logger.LogInformation("Emulator detected - marking transport as initialized (admin verification skipped)");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "emulator_skip");
        return;
      }

      // For production Service Bus, verify connectivity via admin client
      if (_adminClient != null) {
        // Simple connectivity check - try to list namespaces (lightweight operation)
        // This will throw if Service Bus is not reachable
        await _adminClient.GetNamespacePropertiesAsync(cancellationToken);

        _logger.LogInformation("Azure Service Bus transport initialized successfully - connectivity verified");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "admin_api");
      } else {
        // No admin client available - just check if regular client is open
        _logger.LogWarning("Admin client not available - marking transport as initialized without connectivity verification");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "client_open_check");
      }
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to initialize Azure Service Bus transport");
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      throw new InvalidOperationException("Failed to initialize Azure Service Bus transport - Service Bus may not be reachable", ex);
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:Capabilities_WithEnableSessions_IncludesOrderedAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:Capabilities_WithoutEnableSessions_ExcludesOrderedAsync</tests>
  public TransportCapabilities Capabilities =>
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Reliable |
    TransportCapabilities.BulkPublish |
    (_options.EnableSessions ? TransportCapabilities.Ordered : TransportCapabilities.None);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:MaxMessageSizeBytes_Returns256KB_StandardTierCeilingAsync</tests>
  // Azure Service Bus Standard tier hard limit: 256 KB per message including envelope+headers.
  // Premium supports up to 100 MB — consumers running Premium can override at the options layer;
  // we ship the conservative Standard default so out-of-the-box deployments don't silently exceed.
  public long? MaxMessageSizeBytes => 256L * 1024L;

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    ReadOnlyMemory<byte>? preSerializedBytes = null,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);
    return _publishCoreAsync(envelope, destination, envelopeType, preSerializedBytes, cancellationToken);
  }

  private async Task _publishCoreAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType,
    ReadOnlyMemory<byte>? preSerializedBytes,
    CancellationToken cancellationToken
  ) {
    try {
      var sender = await _getOrCreateSenderAsync(destination.Address, cancellationToken);

      // Use provided envelope type name if available, otherwise get it from runtime type
      // IMPORTANT: The envelope object is already correctly typed (MessageEnvelope<JsonElement>), so we serialize using envelope.GetType()
      //            But for METADATA, we use the provided envelopeType string which preserves the original payload type information
      var envelopeTypeName = envelopeType ?? envelope.GetType().AssemblyQualifiedName
        ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

      // For serialization, always use the actual runtime type of the envelope object (AOT-safe)
      var envelopeRuntimeType = envelope.GetType();

      // Honor the upstream pre-serialized hint when present. The publish-side
      // strategy serializes once (for size measurement + post-serialize hooks
      // like body offload) and hands us the final bytes; we MUST use them as-is
      // to preserve the hook chain's substitutions (e.g., claim envelope when
      // the body was offloaded).
      string json;
      if (preSerializedBytes is { } hint) {
        json = Encoding.UTF8.GetString(hint.Span);
      } else {
        // Serialize via the shared WireEnvelopeSerializer (one wire-serialization path). ASB
        // consumes a string body, so decode the bytes — symmetric with the pre-serialized hint
        // path above, which already GetStrings the hint bytes.
        var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
          ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}. Ensure the message type is registered via JsonContextRegistry.");
        var serialized = Whizbang.Core.Serialization.WireEnvelopeSerializer.Serialize(
          envelope, typeInfo, Whizbang.Core.Serialization.SerializationOptions.Default);
        json = Encoding.UTF8.GetString(serialized.Data.Span);
      }

      // DIAGNOSTIC: Log the first 500 chars of JSON to see if MessageId is in there
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId.Value;
        var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
        _logger.LogDebug(
          "DIAGNOSTIC [Publish]: Serialized envelope. MessageId={MessageId}, JSON preview: {JsonPreview}",
          messageId,
          jsonPreview
        );
      }

      var message = new ServiceBusMessage(json) {
        MessageId = envelope.MessageId.Value.ToString(),
        Subject = destination.RoutingKey ?? "message",
        ContentType = "application/json"
      };

      // Set SessionId for FIFO ordering when StreamId is carried in destination metadata
      // TransportPublishStrategy adds StreamId to metadata for transports that support ordering
      if (destination.Metadata?.TryGetValue("StreamId", out var streamIdElement) == true) {
        var streamIdStr = _convertJsonElementToAmqpValue(streamIdElement)?.ToString();
        if (!string.IsNullOrEmpty(streamIdStr)) {
          message.SessionId = streamIdStr;
        }
      }

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[Publish] Setting Subject={Subject} on message {MessageId} to topic {TopicName} (RoutingKey={RoutingKey})",
          message.Subject,
          envelope.MessageId,
          destination.Address,
          destination.RoutingKey ?? "(null)");
      }

      // DIAGNOSTIC: Log the Service Bus message ID to compare
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var serviceBusMessageId = message.MessageId;
        _logger.LogDebug(
          "DIAGNOSTIC [Publish]: Created ServiceBusMessage with MessageId={ServiceBusMessageId}",
          serviceBusMessageId
        );
      }

      // Add envelope type information for deserialization
      message.ApplicationProperties[ENVELOPE_TYPE_PROPERTY] = envelopeTypeName;

      // Add correlation ID if present
      var correlationId = envelope.GetCorrelationId();
      if (correlationId != null) {
        message.CorrelationId = correlationId.Value.Value.ToString();
      }

      // Add causation ID if present
      var causationId = envelope.GetCausationId();
      if (causationId != null) {
        message.ApplicationProperties["CausationId"] = causationId.Value.Value.ToString();
      }

      // Add custom metadata (converting JsonElement to AMQP-compatible primitives)
      if (destination.Metadata != null) {
        foreach (var (key, value) in destination.Metadata) {
          message.ApplicationProperties[key] = _convertJsonElementToAmqpValue(value);
        }
      }

      // WORKAROUND: Azure Service Bus Emulator sometimes hangs on first send
      // Use a task-based timeout instead of CancellationToken (which also hangs)
      // Increased to 30 seconds for emulator (originally 5s, too short for slow emulator with many topics)
      var sendTask = sender.SendMessageAsync(message, cancellationToken);
      var timeoutTask = Task.Delay(_options.SendTimeout, cancellationToken);

      var completedTask = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);

      if (completedTask == timeoutTask) {
        _logger.LogError("DIAGNOSTIC [PublishAsync]: SendMessageAsync timed out after {SendTimeout} for {MessageId} - emulator may not be ready", _options.SendTimeout, envelope.MessageId);
        throw new TimeoutException($"SendMessageAsync timed out after {_options.SendTimeout} for message {envelope.MessageId}. The Azure Service Bus emulator may not be ready or topics/subscriptions may not exist.");
      }

      await sendTask; // Re-await to propagate exceptions

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId;
        var topicName = destination.Address;
        var subject = message.Subject;
        _logger.LogDebug(
          "Published message {MessageId} to topic {TopicName} with subject {Subject}",
          messageId,
          topicName,
          subject
        );
      }
#pragma warning disable S2139 // Intentional log-and-rethrow: transport errors cross async/DI boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to publish message {MessageId} to {Destination}",
        envelope.MessageId,
        destination.Address
      );
      throw;
#pragma warning restore S2139
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
    IReadOnlyList<BulkPublishItem> items,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(items);
    ArgumentNullException.ThrowIfNull(destination);

    if (items.Count == 0) {
      return [];
    }

    var sender = await _getOrCreateSenderAsync(destination.Address, cancellationToken);

    // Group items by StreamId to ensure messages in the same session go into the same batch.
    // ASB requires all messages in a ServiceBusMessageBatch to have the same SessionId
    // when sessions are enabled. Null StreamId items group together (no session requirement).
    var streamGroups = items
      .Select((item, index) => (Item: item, OriginalIndex: index))
      .GroupBy(x => x.Item.StreamId)
      .ToList();

    // Parallelize across stream groups. ServiceBusSender is thread-safe, and each group's
    // ServiceBusMessageBatch is built/sent independently. Per-group logic stays serial so
    // Session semantics (fill-batch-then-send) are preserved. Concurrency is bounded by
    // AzureServiceBusOptions.PublishMaxConcurrency (default 200) — see XML doc for tuning.
    var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<BulkPublishItemResult>();

    await Parallel.ForEachAsync(
      streamGroups,
      new ParallelOptions { MaxDegreeOfParallelism = _options.PublishMaxConcurrency, CancellationToken = cancellationToken },
      async (streamGroup, ct) => {
        var groupItems = streamGroup.ToList();
        var localResults = new List<BulkPublishItemResult>(groupItems.Count);

        var currentBatch = await sender.CreateMessageBatchAsync(ct);
        var batchItemIds = new List<Guid>();

        foreach (var (item, _) in groupItems) {
          try {
            var message = _createServiceBusMessage(item, destination);

            if (!currentBatch.TryAddMessage(message)) {
              // Current batch is full -- send and start new
              await _sendAndRecordBatchAsync(sender, currentBatch, batchItemIds, localResults, ct);
              currentBatch = await sender.CreateMessageBatchAsync(ct);
              batchItemIds = [];

              if (!currentBatch.TryAddMessage(message)) {
                localResults.Add(new BulkPublishItemResult {
                  MessageId = item.MessageId,
                  Success = false,
                  Error = $"Message {item.MessageId} exceeds maximum batch message size"
                });
                continue;
              }
            }

            batchItemIds.Add(item.MessageId);
          } catch (Exception ex) when (ex is not OperationCanceledException) {
            localResults.Add(new BulkPublishItemResult {
              MessageId = item.MessageId,
              Success = false,
              Error = $"{ex.GetType().Name}: {ex.Message}"
            });
          }
        }

        // Send remaining batch for this stream group
        await _sendAndRecordBatchAsync(sender, currentBatch, batchItemIds, localResults, ct);

        foreach (var r in localResults) {
          concurrentResults.Add(r);
        }
      }
    );

    return [.. concurrentResults];
  }

  private static async Task _sendAndRecordBatchAsync(
    ServiceBusSender sender,
    ServiceBusMessageBatch batch,
    List<Guid> batchItemIds,
    List<BulkPublishItemResult> results,
    CancellationToken cancellationToken) {

    if (batch.Count == 0) {
      return;
    }

    try {
      await sender.SendMessagesAsync(batch, cancellationToken);
      foreach (var id in batchItemIds) {
        results.Add(new BulkPublishItemResult { MessageId = id, Success = true });
      }
    } catch (Exception ex) {
      foreach (var id in batchItemIds) {
        results.Add(new BulkPublishItemResult {
          MessageId = id,
          Success = false,
          Error = $"{ex.GetType().Name}: {ex.Message}"
        });
      }
    }
  }

  private ServiceBusMessage _createServiceBusMessage(BulkPublishItem item, TransportDestination destination) {
    var envelope = item.Envelope;
    var envelopeTypeName = item.EnvelopeType ?? envelope.GetType().AssemblyQualifiedName
      ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

    var envelopeRuntimeType = envelope.GetType();

    // Honor per-item pre-serialized hint when present (avoids double-serialize
    // when the publish strategy ran the post-serialize hook chain).
    string json;
    if (item.PreSerializedBytes is { } hint) {
      json = Encoding.UTF8.GetString(hint.Span);
    } else {
      // Shared wire-serialization path; ASB consumes a string body (symmetric with the hint above).
      var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
        ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}. Ensure the message type is registered via JsonContextRegistry.");
      var serialized = Whizbang.Core.Serialization.WireEnvelopeSerializer.Serialize(
        envelope, typeInfo, Whizbang.Core.Serialization.SerializationOptions.Default);
      json = Encoding.UTF8.GetString(serialized.Data.Span);
    }

    var message = new ServiceBusMessage(json) {
      MessageId = envelope.MessageId.Value.ToString(),
      Subject = item.RoutingKey ?? destination.RoutingKey ?? "message",
      ContentType = "application/json"
    };

    // Set SessionId for FIFO ordering when StreamId is present
    // ASB delivers messages with the same SessionId in order to a single consumer
    if (item.StreamId.HasValue) {
      message.SessionId = item.StreamId.Value.ToString();
    }

    message.ApplicationProperties[ENVELOPE_TYPE_PROPERTY] = envelopeTypeName;

    var correlationId = envelope.GetCorrelationId();
    if (correlationId != null) {
      message.CorrelationId = correlationId.Value.Value.ToString();
    }

    var causationId = envelope.GetCausationId();
    if (causationId != null) {
      message.ApplicationProperties["CausationId"] = causationId.Value.Value.ToString();
    }

    if (destination.Metadata != null) {
      foreach (var (key, value) in destination.Metadata) {
        message.ApplicationProperties[key] = _convertJsonElementToAmqpValue(value);
      }
    }

    // Per-item metadata overrides shared destination metadata for the same key
    // (e.g., whizbang.body-size, whizbang.is-claim) — that's the contract.
    if (item.PerItemMetadata != null) {
      foreach (var (key, value) in item.PerItemMetadata) {
        message.ApplicationProperties[key] = _convertJsonElementToAmqpValue(value);
      }
    }

    return message;
  }

  /// <inheritdoc />
  /// <docs>messaging/transports/azure-service-bus#batch-subscribe</docs>
  public Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(batchHandler);
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentNullException.ThrowIfNull(batchOptions);

    return _subscribeBatchCoreAsync(batchHandler, destination, batchOptions, cancellationToken);
  }

  private readonly record struct PendingServiceBusMessage(ProcessMessageEventArgs Args);

  private async Task<ISubscription> _subscribeBatchCoreAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken
  ) {
    try {
      var topicName = destination.Address;
      var subscriptionName = _deriveSubscriptionName(destination, topicName);

      await _ensureInfrastructureExistsAsync(topicName, subscriptionName, cancellationToken);
      await _applySubscriptionFiltersAsync(destination, topicName, subscriptionName, cancellationToken);

      var subscription = _options.EnableSessions
        ? await _startSessionBatchSubscriptionAsync(
            topicName, subscriptionName, batchHandler, destination, cancellationToken)
        : await _startNonSessionBatchSubscriptionAsync(
            topicName, subscriptionName, batchHandler, batchOptions, destination, cancellationToken);

      if (_logger.IsEnabled(LogLevel.Information)) {
        _logger.LogInformation(
          "Started batch subscription to {TopicName}/{SubscriptionName} (BatchSize={BatchSize}, SlideMs={SlideMs}, MaxWaitMs={MaxWaitMs})",
          topicName, subscriptionName, batchOptions.BatchSize, batchOptions.SlideMs, batchOptions.MaxWaitMs);
      }

      return subscription;
#pragma warning disable S2139
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to create batch subscription to {TopicName}/{SubscriptionName}",
        destination.Address, destination.RoutingKey ?? _options.DefaultSubscriptionName);
      throw;
#pragma warning restore S2139
    }
  }

  /// <summary>
  /// Session-enabled batch subscription. ProcessSessionMessageEventArgs is a different type
  /// from ProcessMessageEventArgs, so we fall back to per-message handling with single-item
  /// batches. AutoCompleteMessages is disabled — the handler completes on success.
  /// </summary>
  private async Task<AzureServiceBusSubscription> _startSessionBatchSubscriptionAsync(
    string topicName,
    string subscriptionName,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    CancellationToken cancellationToken
  ) {
    var sessionProcessorOptions = new ServiceBusSessionProcessorOptions {
      MaxConcurrentSessions = _options.MaxConcurrentSessions,
      MaxConcurrentCallsPerSession = 1,
      AutoCompleteMessages = false,
      MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
      PrefetchCount = _options.PrefetchCount,
      SessionIdleTimeout = _options.SessionIdleTimeout
    };

    var sessionProcessor = _client.CreateSessionProcessor(topicName, subscriptionName, sessionProcessorOptions);
    var subscription = new AzureServiceBusSubscription(sessionProcessor, _logger);

    sessionProcessor.ProcessMessageAsync += args => _handleSessionBatchMessageAsync(args, batchHandler, destination, subscription);
    sessionProcessor.ProcessErrorAsync += args => _handleProcessorErrorAsync(args, destination);

    await sessionProcessor.StartProcessingAsync(cancellationToken);
    return subscription;
  }

  /// <summary>
  /// Per-message handler for session mode. Abandons when subscription is inactive, deserializes
  /// and dispatches single-item batches, and completes on success. Errors are routed through the
  /// session-specific error handler (which manages session-lock semantics).
  /// </summary>
  private async Task _handleSessionBatchMessageAsync(
    ProcessSessionMessageEventArgs args,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    AzureServiceBusSubscription subscription
  ) {
    if (!subscription.IsActive) {
      await _safeAbandonAsync(args);
      return;
    }

    try {
      var (envelope, envelopeTypeName) = await _deserializeReceivedSessionMessageAsync(args, destination);
      if (envelope is null) {
        return;
      }

      await batchHandler([new TransportMessage(envelope, envelopeTypeName)], args.CancellationToken);
      await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);
    } catch (Exception ex) {
      await _handleSessionMessageProcessingErrorAsync(args, ex, destination);
    }
  }

  /// <summary>
  /// Non-session batch subscription. Per-message callbacks enqueue to the TransportBatchCollector,
  /// which flushes to the batch handler on size/slide/maxWait trigger. Completion is per-message
  /// (ASB has no multi-ACK) and runs after the handler returns.
  /// </summary>
  private async Task<AzureServiceBusSubscription> _startNonSessionBatchSubscriptionAsync(
    string topicName,
    string subscriptionName,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportBatchOptions batchOptions,
    TransportDestination destination,
    CancellationToken cancellationToken
  ) {
    var collector = _buildPendingMessageCollector(batchHandler, batchOptions, destination);

    var processorOptions = new ServiceBusProcessorOptions {
      MaxConcurrentCalls = _options.MaxConcurrentCalls,
      AutoCompleteMessages = false,
      MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
      PrefetchCount = _options.PrefetchCount
    };

    var processor = _client.CreateProcessor(topicName, subscriptionName, processorOptions);
    var subscription = new AzureServiceBusSubscription(processor, _logger);

    // Non-blocking: enqueue to collector, return immediately
    processor.ProcessMessageAsync += args => {
      if (!subscription.IsActive) {
        return args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
      }
      collector.Enqueue(new PendingServiceBusMessage(args));
      return Task.CompletedTask;
    };

    processor.ProcessErrorAsync += args => _handleProcessorErrorAsync(args, destination);

    await processor.StartProcessingAsync(cancellationToken);
    return subscription;
  }

  /// <summary>
  /// Builds a TransportBatchCollector whose flush callback deserializes each pending ServiceBus
  /// message, invokes the batch handler, and then completes the successful ones. Messages that
  /// fail deserialization are already dead-lettered by _deserializeReceivedMessageAsync.
  /// </summary>
  private TransportBatchCollector<PendingServiceBusMessage> _buildPendingMessageCollector(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportBatchOptions batchOptions,
    TransportDestination destination
  ) {
    return new TransportBatchCollector<PendingServiceBusMessage>(
      batchOptions,
      async batch => {
        var transportMessages = new List<TransportMessage>(batch.Count);
        var successfulArgs = new List<ProcessMessageEventArgs>(batch.Count);

        // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
        foreach (var pending in batch) {
          var (envelope, envelopeTypeName) = await _deserializeReceivedMessageAsync(pending.Args, destination);
          if (envelope is not null) {
            transportMessages.Add(new TransportMessage(envelope, envelopeTypeName));
            successfulArgs.Add(pending.Args);
          }
        }
#pragma warning restore S3267

        if (transportMessages.Count == 0) {
          return;
        }

        await batchHandler(transportMessages, CancellationToken.None);

        // Per-message CompleteMessageAsync (ASB has no multi-ACK)
        foreach (var args in successfulArgs) {
          try {
            await args.CompleteMessageAsync(args.Message, cancellationToken: CancellationToken.None);
          } catch (ServiceBusException ex) {
            _logger.LogWarning(ex,
              "Failed to complete message {MessageId} — will be redelivered after lock expiry",
              args.Message.MessageId);
          }
        }
      }
    );
  }

  // Keep SubscribeAsync as internal for backward compat during migration
  internal Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(destination);
    return _subscribeCoreAsync(handler, destination, cancellationToken);
  }

  private async Task<ISubscription> _subscribeCoreAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken
  ) {
    try {
      var topicName = destination.Address;

      // FIXED: Derive subscription name from SubscriberName metadata, NOT from RoutingKey
      // The Core layer sets RoutingKey="#" for "subscribe to all" which is invalid for ASB
      var subscriptionName = _deriveSubscriptionName(destination, topicName);

      // Ensure infrastructure exists when auto-provisioning is enabled
      await _ensureInfrastructureExistsAsync(topicName, subscriptionName, cancellationToken);

      // Apply routing and correlation filters from metadata
      await _applySubscriptionFiltersAsync(destination, topicName, subscriptionName, cancellationToken);

      AzureServiceBusSubscription subscription;

      if (_options.EnableSessions) {
        // Create session processor for FIFO ordering
        var sessionProcessorOptions = new ServiceBusSessionProcessorOptions {
          MaxConcurrentSessions = _options.MaxConcurrentSessions,
          MaxConcurrentCallsPerSession = 1, // Strict FIFO within each session
          AutoCompleteMessages = false,
          MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
          PrefetchCount = _options.PrefetchCount,
          SessionIdleTimeout = _options.SessionIdleTimeout
        };

        var sessionProcessor = _client.CreateSessionProcessor(
          topicName,
          subscriptionName,
          sessionProcessorOptions
        );

        subscription = new AzureServiceBusSubscription(sessionProcessor, _logger);

        sessionProcessor.ProcessMessageAsync += async args => {
          if (!subscription.IsActive) {
            _logger.LogWarning(
              "ABANDON reason: Subscription paused - requeueing message {MessageId} from {TopicName}/{SubscriptionName}",
              args.Message.MessageId,
              destination.Address,
              destination.RoutingKey ?? _options.DefaultSubscriptionName
            );
            await _safeAbandonAsync(args);
            return;
          }

          await _processReceivedMessageAsync(args, handler, destination);
        };

        sessionProcessor.ProcessErrorAsync += async args => await _handleProcessorErrorAsync(args, destination);

        await sessionProcessor.StartProcessingAsync(cancellationToken);
      } else {
        // Create standard processor (no session/FIFO guarantees)
        var processorOptions = new ServiceBusProcessorOptions {
          MaxConcurrentCalls = _options.MaxConcurrentCalls,
          AutoCompleteMessages = false,
          MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
          PrefetchCount = _options.PrefetchCount
        };

        var processor = _client.CreateProcessor(
          topicName,
          subscriptionName,
          processorOptions
        );

        subscription = new AzureServiceBusSubscription(processor, _logger);

        processor.ProcessMessageAsync += async args => {
          if (!subscription.IsActive) {
            _logger.LogWarning(
              "ABANDON reason: Subscription paused - requeueing message {MessageId} from {TopicName}/{SubscriptionName}",
              args.Message.MessageId,
              destination.Address,
              destination.RoutingKey ?? _options.DefaultSubscriptionName
            );
            await _safeAbandonAsync(args);
            return;
          }

          await _processReceivedMessageAsync(args, handler, destination);
        };

        processor.ProcessErrorAsync += async args => await _handleProcessorErrorAsync(args, destination);

        await processor.StartProcessingAsync(cancellationToken);
      }

      if (_logger.IsEnabled(LogLevel.Information)) {
        var topic = destination.Address;
        var sub = destination.RoutingKey ?? _options.DefaultSubscriptionName;
        _logger.LogInformation(
          "Started subscription to {TopicName}/{SubscriptionName}",
          topic,
          sub
        );
      }

      return subscription;
#pragma warning disable S2139 // Intentional log-and-rethrow: subscription failures cross async/worker boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to create subscription to {TopicName}/{SubscriptionName}",
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      throw;
#pragma warning restore S2139
    }
  }

  private async Task _processReceivedMessageAsync(
    ProcessMessageEventArgs args,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination
      ) {
    try {
      var (envelope, envelopeTypeName) = await _deserializeReceivedMessageAsync(args, destination);
      if (envelope is null) {
        return; // Message was dead-lettered by _deserializeReceivedMessageAsync
      }

      await handler(envelope, envelopeTypeName, args.CancellationToken);
      await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Processed message {MessageId} from {TopicName}/{SubscriptionName}",
          args.Message.MessageId,
          destination.Address,
          destination.RoutingKey ?? _options.DefaultSubscriptionName
        );
      }
    } catch (Exception ex) {
      await _handleMessageProcessingErrorAsync(args, ex, destination);
    }
  }

  // S4136: Session overload kept adjacent to the non-session overload above.
  // ProcessSessionMessageEventArgs does not inherit from ProcessMessageEventArgs,
  // so we need separate overloads for session-aware message handling.
  private async Task _processReceivedMessageAsync(
    ProcessSessionMessageEventArgs args,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination
  ) {
    try {
      var (envelope, envelopeTypeName) = await _deserializeReceivedSessionMessageAsync(args, destination);
      if (envelope is null) {
        return;
      }

      await handler(envelope, envelopeTypeName, args.CancellationToken);
      await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Processed session message {MessageId} (SessionId={SessionId}) from {TopicName}/{SubscriptionName}",
          args.Message.MessageId,
          args.SessionId,
          destination.Address,
          destination.RoutingKey ?? _options.DefaultSubscriptionName
        );
      }
    } catch (Exception ex) {
      await _handleSessionMessageProcessingErrorAsync(args, ex, destination);
    }
  }

  private async Task<(IMessageEnvelope? Envelope, string? EnvelopeTypeName)> _deserializeReceivedMessageAsync(
    ProcessMessageEventArgs args,
    TransportDestination destination
  ) {
    var json = args.Message.Body.ToString();

    if (_logger.IsEnabled(LogLevel.Debug)) {
      var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
      _logger.LogDebug(
        "DIAGNOSTIC [Subscribe]: Received message. ServiceBusMessageId={ServiceBusMessageId}, JSON preview: {JsonPreview}",
        args.Message.MessageId,
        jsonPreview
      );
    }

    var decision = _decisionMaker.Decide(
      args.Message.ApplicationProperties,
      json,
      Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName,
      _jsonOptions,
      _buildIsHandledLocally(),
      _rawReceptorRegistry,
      _typeBinder,
      absorbedNamespaces: _absorbedNamespaces);

    var subscription = destination.RoutingKey ?? _options.DefaultSubscriptionName;

    switch (decision.Action) {
      case AsbReceiveAction.Process:
        if (_logger.IsEnabled(LogLevel.Debug) && decision.Envelope != null) {
          _logger.LogDebug(
            "DIAGNOSTIC [Subscribe]: Deserialized envelope. MessageId={MessageId}",
            decision.Envelope.MessageId.Value
          );
        }
        return (decision.Envelope, decision.EnvelopeTypeName);

      case AsbReceiveAction.AckAndDrop:
        // Slice 1 hotfix: do NOT dead-letter on type-resolution failures. Ack the broker so
        // the message exits the topic; structured warning + metric so operators see the drop
        // rate without ASB DLQ accumulation.
        _logger.LogWarning(
          "ASB receive ack+drop. Reason={Reason} EnvelopeType={EnvelopeType} MessageId={MessageId} Topic={TopicName} Subscription={SubscriptionName} Detail={Description}",
          decision.Reason,
          decision.EnvelopeTypeName,
          args.Message.MessageId,
          destination.Address,
          subscription,
          decision.Description
        );
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        return (null, decision.EnvelopeTypeName);

      case AsbReceiveAction.InvokeRawReceptor:
        // Slice 5 — typed binder couldn't resolve, but a raw receptor opted in for this
        // message type. Dispatch with the raw JsonElement payload, then ack.
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation(
            "ASB receive raw-receptor. EnvelopeType={EnvelopeType} MessageId={MessageId} Detail={Description}",
            decision.EnvelopeTypeName, args.Message.MessageId, decision.Description);
        }
        if (decision.RawReceptor != null && decision.RawPayload.HasValue) {
          await decision.RawReceptor.HandleAsync(decision.RawPayload.Value, args.CancellationToken);
        }
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        return (null, decision.EnvelopeTypeName);

      case AsbReceiveAction.DeadLetter:
        _logger.LogWarning(
          "DEAD-LETTER reason: {Reason} for message {MessageId} from {TopicName}/{SubscriptionName}",
          decision.Reason,
          args.Message.MessageId,
          destination.Address,
          subscription
        );
        await _safeDeadLetterAsync(args, decision.Reason, decision.Description);
        return (null, decision.EnvelopeTypeName);

      default:
        throw new InvalidOperationException($"Unknown AsbReceiveAction: {decision.Action}");
    }
  }

  // ========================================
  // SESSION MESSAGE PROCESSING OVERLOADS
  // ProcessSessionMessageEventArgs does not inherit from ProcessMessageEventArgs,
  // so we need separate overloads for session-aware message handling.
  // (Process overload moved adjacent to non-session overload above to satisfy S4136.)
  // ========================================

  private async Task<(IMessageEnvelope? Envelope, string? EnvelopeTypeName)> _deserializeReceivedSessionMessageAsync(
    ProcessSessionMessageEventArgs args,
    TransportDestination destination
  ) {
    var json = args.Message.Body.ToString();

    var decision = _decisionMaker.Decide(
      args.Message.ApplicationProperties,
      json,
      Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName,
      _jsonOptions,
      _buildIsHandledLocally(),
      _rawReceptorRegistry,
      _typeBinder,
      absorbedNamespaces: _absorbedNamespaces);

    var subscription = destination.RoutingKey ?? _options.DefaultSubscriptionName;

    switch (decision.Action) {
      case AsbReceiveAction.Process:
        return (decision.Envelope, decision.EnvelopeTypeName);

      case AsbReceiveAction.AckAndDrop:
        // Slice 1 hotfix: ack + drop instead of broker DLQ on type-resolution failures.
        // Slice 2 (discard-policy): NoLocalConsumer is a routine cross-domain drop and
        // routes through the policy (Debug + counter) instead of WARN logspam. All
        // other AckAndDrop reasons (genuine envelope/type failures) still warn.
        EmitAckDropTelemetry(
          transportLogger: _logger,
          discardPolicy: _discardPolicy,
          decision: decision,
          messageId: args.Message.MessageId,
          sessionId: args.SessionId,
          topic: destination.Address,
          subscription: subscription);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        return (null, decision.EnvelopeTypeName);

      case AsbReceiveAction.InvokeRawReceptor:
        // Slice 5 — raw receptor wants this message even though the typed binder missed.
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation(
            "ASB session-receive raw-receptor. EnvelopeType={EnvelopeType} MessageId={MessageId} SessionId={SessionId} Detail={Description}",
            decision.EnvelopeTypeName, args.Message.MessageId, args.SessionId, decision.Description);
        }
        if (decision.RawReceptor != null && decision.RawPayload.HasValue) {
          await decision.RawReceptor.HandleAsync(decision.RawPayload.Value, args.CancellationToken);
        }
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        return (null, decision.EnvelopeTypeName);

      case AsbReceiveAction.DeadLetter:
        _logger.LogWarning(
          "DEAD-LETTER reason: {Reason} for session message {MessageId} (SessionId={SessionId}) from {TopicName}/{SubscriptionName}",
          decision.Reason,
          args.Message.MessageId,
          args.SessionId,
          destination.Address,
          subscription
        );
        await _safeDeadLetterAsync(args, decision.Reason, decision.Description);
        return (null, decision.EnvelopeTypeName);

      default:
        throw new InvalidOperationException($"Unknown AsbReceiveAction: {decision.Action}");
    }
  }

  private async Task _handleSessionMessageProcessingErrorAsync(
    ProcessSessionMessageEventArgs args,
    Exception ex,
    TransportDestination destination
  ) {
    _logger.LogError(
      ex,
      "Error processing session message {MessageId} (SessionId={SessionId}) from {TopicName}/{SubscriptionName}",
      args.Message.MessageId,
      args.SessionId,
      destination.Address,
      destination.RoutingKey ?? _options.DefaultSubscriptionName
    );

    var deliveryCount = args.Message.DeliveryCount;
    if (deliveryCount >= _options.MaxDeliveryAttempts) {
      await _safeDeadLetterAsync(args, "MaxDeliveryAttemptsExceeded", ex.Message);
    } else {
      await _safeAbandonAsync(args);
    }
  }

  private async Task _handleMessageProcessingErrorAsync(
    ProcessMessageEventArgs args,
    Exception ex,
    TransportDestination destination
  ) {
    _logger.LogError(
      ex,
      "Error processing message {MessageId} from {TopicName}/{SubscriptionName}",
      args.Message.MessageId,
      destination.Address,
      destination.RoutingKey ?? _options.DefaultSubscriptionName
    );

    var deliveryCount = args.Message.DeliveryCount;
    if (deliveryCount >= _options.MaxDeliveryAttempts) {
      _logger.LogWarning(
        "DEAD-LETTER reason: Handler exception after max delivery attempts ({DeliveryCount}/{MaxAttempts}) for message {MessageId} from {TopicName}/{SubscriptionName}. Exception: {ExceptionType}: {ExceptionMessage}",
        deliveryCount,
        _options.MaxDeliveryAttempts,
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName,
        ex.GetType().Name,
        ex.Message
      );
      await _safeDeadLetterAsync(args, "MaxDeliveryAttemptsExceeded", ex.Message);
    } else {
      _logger.LogWarning(
        "ABANDON reason: Handler exception (attempt {DeliveryCount}/{MaxAttempts}) for message {MessageId} from {TopicName}/{SubscriptionName} - requeueing for retry. Exception: {ExceptionType}: {ExceptionMessage}",
        deliveryCount,
        _options.MaxDeliveryAttempts,
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName,
        ex.GetType().Name,
        ex.Message
      );
      await _safeAbandonAsync(args);
    }
  }

  private async Task _handleProcessorErrorAsync(
    ProcessErrorEventArgs args,
    TransportDestination destination
  ) {
    _logger.LogError(
      args.Exception,
      "Error in Service Bus processor for {TopicName}/{SubscriptionName}: {ErrorSource}",
      destination.Address,
      destination.RoutingKey ?? _options.DefaultSubscriptionName,
      args.ErrorSource
    );

    if (_isConnectionError(args.Exception)) {
      _logger.LogWarning(
        "Detected connection-level error in Service Bus processor, triggering recovery: {ErrorReason}",
        (args.Exception as ServiceBusException)?.Reason
      );
      await _invokeRecoveryHandlerAsync();
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Azure Service Bus doesn't natively support request/response pattern
    // This would typically be implemented using sessions or request/response store
    // For now, throw not supported
    throw new NotSupportedException(
      "Request/response pattern is not supported by Azure Service Bus transport. " +
      "Use IRequestResponseStore with publish/subscribe instead."
    );
  }

  private async Task _applySubscriptionFiltersAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    // DIAGNOSTIC: Log metadata to trace RoutingPatterns flow
    _logger.LogWarning(
      "DIAGNOSTIC [SubscribeAsync]: Topic={TopicName}, Subscription={SubscriptionName}, MetadataNull={MetadataNull}, MetadataKeys=[{MetadataKeys}]",
      topicName,
      subscriptionName,
      destination.Metadata == null,
      destination.Metadata != null ? string.Join(", ", destination.Metadata.Keys) : "N/A");

    // Apply routing pattern filter if RoutingPatterns metadata exists
    await _applyRoutingPatternsFromMetadataAsync(destination, topicName, subscriptionName, cancellationToken);

    // Apply CorrelationFilter if specified in metadata (production without Aspire)
    await _applyCorrelationFilterFromMetadataAsync(destination, topicName, subscriptionName, cancellationToken);
  }

  private async Task _applyRoutingPatternsFromMetadataAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    if (destination.Metadata?.TryGetValue("RoutingPatterns", out var routingPatternsElement) != true) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[Subscribe] RoutingPatterns not found in metadata for {TopicName}/{SubscriptionName}",
          topicName,
          subscriptionName);
      }
      return;
    }

    if (routingPatternsElement.ValueKind != JsonValueKind.Array) {
      return;
    }

    var patterns = new List<string>();
    foreach (var pattern in routingPatternsElement.EnumerateArray()) {
      if (pattern.ValueKind == JsonValueKind.String) {
        var patternStr = pattern.GetString();
        if (!string.IsNullOrWhiteSpace(patternStr)) {
          patterns.Add(patternStr);
        }
      }
    }
    if (patterns.Count > 0) {
      await _applyRoutingPatternFilterAsync(topicName, subscriptionName, patterns, cancellationToken);
    }
  }

  private async Task _applyCorrelationFilterFromMetadataAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    // Skip if emulator (filters provisioned by Aspire AppHost)
    if (_isEmulator) {
      return;
    }

    if (destination.Metadata?.TryGetValue("DestinationFilter", out var destinationFilterElem) != true ||
        destinationFilterElem.ValueKind != JsonValueKind.String) {
      return;
    }

    var destinationFilter = destinationFilterElem.GetString();
    if (string.IsNullOrWhiteSpace(destinationFilter)) {
      return;
    }

    if (_adminClient != null) {
      try {
        await _applyCorrelationFilterAsync(topicName, subscriptionName, destinationFilter, cancellationToken);
      } catch (Exception ex) {
        _logger.LogWarning(
          ex,
          "DestinationFilter '{DestinationFilter}' for {TopicName}/{SubscriptionName}: failed to apply, proceeding without filter",
          destinationFilter,
          topicName,
          subscriptionName
        );
      }
    } else if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug(
        "DestinationFilter '{DestinationFilter}' specified for {TopicName}/{SubscriptionName} but administration client is not available",
        destinationFilter,
        topicName,
        subscriptionName
      );
    }
  }

  /// <summary>
  /// Applies a CorrelationFilter to a subscription by replacing the default rule.
  /// Filters messages based on the Destination application property.
  /// </summary>
  private async Task _applyCorrelationFilterAsync(
    string topicName,
    string subscriptionName,
    string destination,
    CancellationToken cancellationToken
  ) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ApplyCorrelationFilter");
    activity?.SetTag("servicebus.topic", topicName);
    activity?.SetTag("servicebus.subscription", subscriptionName);
    activity?.SetTag("servicebus.filter_type", "CorrelationRuleFilter");
    activity?.SetTag("servicebus.destination", destination);

    if (_adminClient == null) {
      throw new InvalidOperationException("Administration client is not available");
    }

    const string defaultRuleName = "$Default";
    const string customRuleName = "DestinationFilter";

    try {
      // Check if subscription exists
      var subscriptionExists = await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken);
      if (!subscriptionExists) {
        activity?.SetTag("servicebus.subscription_exists", false);
        _logger.LogWarning(
          "Subscription {TopicName}/{SubscriptionName} does not exist. Cannot apply CorrelationFilter.",
          topicName,
          subscriptionName
        );
        return;
      }
      activity?.SetTag("servicebus.subscription_exists", true);

      // Idempotency: an existing DestinationFilter already targeting this destination — with no
      // $Default match-all alongside it — needs no churn. The delete-create gap would briefly
      // leave the subscription without its filter (see _applyRoutingPatternFilterAsync).
      var existingRules = new List<RuleProperties>();
      await foreach (var rule in _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        existingRules.Add(rule);
      }
      var currentFilter = existingRules.FirstOrDefault(r => r.Name == customRuleName);
      if (currentFilter is not null
          && !existingRules.Any(r => r.Name == defaultRuleName)
          && currentFilter.Filter is CorrelationRuleFilter existingCorrelation
          && existingCorrelation.ApplicationProperties.TryGetValue("Destination", out var existingDestination)
          && Equals(existingDestination, destination)) {
        activity?.SetTag("servicebus.rule_unchanged", true);
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug(
            "DestinationFilter already correct on {TopicName}/{SubscriptionName}; skipping re-application",
            topicName,
            subscriptionName);
        }
        return;
      }

      // Remove default rule if it exists
      var deletedRules = 0;
      foreach (var rule in existingRules) {
        if (rule.Name == defaultRuleName || rule.Name == customRuleName) {
          await _adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
          deletedRules++;
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var ruleName = rule.Name;
            var topic = topicName;
            var subscription = subscriptionName;
            _logger.LogDebug(
              "Deleted rule '{RuleName}' from {TopicName}/{SubscriptionName}",
              ruleName,
              topic,
              subscription
            );
          }
        }
      }
      activity?.SetTag("servicebus.rules_deleted", deletedRules);

      // Create new rule with CorrelationFilter on Destination application property
      var ruleOptions = new CreateRuleOptions {
        Name = customRuleName,
        Filter = new CorrelationRuleFilter {
          ApplicationProperties = { ["Destination"] = destination }
        }
      };

      await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);
      activity?.SetTag("servicebus.rule_created", true);

      if (_logger.IsEnabled(LogLevel.Information)) {
        var dest = destination;
        var topic = topicName;
        var subscription = subscriptionName;
        _logger.LogInformation(
          "Applied CorrelationFilter for Destination='{Destination}' to {TopicName}/{SubscriptionName}",
          dest,
          topic,
          subscription
        );
      }
#pragma warning disable S2139 // Intentional log-and-rethrow: infrastructure provisioning errors cross async/DI boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _logger.LogError(
        ex,
        "Error applying CorrelationFilter for Destination='{Destination}' to {TopicName}/{SubscriptionName}",
        destination,
        topicName,
        subscriptionName
      );
      throw;
#pragma warning restore S2139
    }
  }

  #region Subscription Name Derivation

  /// <summary>
  /// Derives subscription name from SubscriberName metadata, NOT RoutingKey.
  /// The Core layer sets RoutingKey for routing patterns (e.g., "#" for all messages),
  /// which are invalid for Azure Service Bus subscription names.
  /// </summary>
  /// <param name="destination">The transport destination containing metadata.</param>
  /// <param name="topicName">The topic name being subscribed to.</param>
  /// <returns>A valid Azure Service Bus subscription name.</returns>
  /// <docs>messaging/transports/azure-service-bus#subscription-naming</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs:DeriveSubscriptionNameWithSubscriberNameMetadataUsesServiceNameAndTopicAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs:DeriveSubscriptionNameWithHashWildcardDoesNotUseAsSubscriptionNameAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_SubscriberNameMetadata_DerivesSubscriptionNameAsync</tests>
  private string _deriveSubscriptionName(TransportDestination destination, string topicName) {
    // Try to get SubscriberName from metadata (set by TransportSubscriptionBuilder)
    if (destination.Metadata?.TryGetValue("SubscriberName", out var elem) == true &&
        elem.ValueKind == JsonValueKind.String) {
      var subscriberName = elem.GetString();
      if (!string.IsNullOrWhiteSpace(subscriberName)) {
        var derivedName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug(
            "Derived subscription name '{SubscriptionName}' from SubscriberName metadata '{SubscriberName}' for topic '{TopicName}'",
            derivedName,
            subscriberName,
            topicName
          );
        }
        return derivedName;
      }
    }

    // Fallback - use routing key if it's a valid subscription name (no wildcards)
    var routingKey = destination.RoutingKey;
    if (!string.IsNullOrWhiteSpace(routingKey) && !_isWildcardPattern(routingKey)) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Using RoutingKey '{RoutingKey}' as subscription name for topic '{TopicName}'",
          routingKey,
          topicName
        );
      }
      return routingKey;
    }

    // Final fallback - use default subscription name
    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug(
        "Using default subscription name '{DefaultName}' for topic '{TopicName}' (RoutingKey '{RoutingKey}' is wildcard or empty)",
        _options.DefaultSubscriptionName,
        topicName,
        routingKey ?? "(null)"
      );
    }
    return _options.DefaultSubscriptionName;
  }

  /// <summary>
  /// Determines if a routing key contains wildcard patterns that are invalid for subscription names.
  /// </summary>
  /// <param name="routingKey">The routing key to check.</param>
  /// <returns>True if the routing key contains wildcard characters.</returns>
  private static bool _isWildcardPattern(string routingKey) =>
    routingKey.Contains('#') || routingKey.Contains('*') || routingKey.Contains(',');

  #endregion

  #region Infrastructure Provisioning

  /// <summary>
  /// Ensures topic and subscription exist when AutoProvisionInfrastructure is enabled.
  /// Handles race conditions gracefully by ignoring 409 Conflict errors.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>messaging/transports/azure-service-bus#auto-provisioning</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_TopicAndSubscriptionMissing_CreatesBothAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_AutoProvisionDisabled_DoesNotTouchAdminPlaneAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_TopicCreate409Race_ContinuesToSubscriptionCreationAsync</tests>
  private async Task _ensureInfrastructureExistsAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken) {

    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    // Ensure topic exists
    await _ensureTopicExistsViaAdminAsync(topicName, cancellationToken);

    // Ensure subscription exists (with session auto-migration when EnableSessions is true)
    try {
      var subscriptionExists = await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken);
      if (subscriptionExists) {
        await _migrateSubscriptionToSessionsIfNeededAsync(topicName, subscriptionName, cancellationToken);
      } else {
        await _createSubscriptionAsync(topicName, subscriptionName, cancellationToken);
      }
    } catch (Azure.RequestFailedException ex) when (ex.Status == 409) {
      // Race condition - subscription created by another instance, safe to ignore
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Subscription {TopicName}/{SubscriptionName} already exists (409 conflict)", topicName, subscriptionName);
      }
    }
  }

  /// <summary>
  /// When EnableSessions is true and the existing subscription doesn't require sessions,
  /// delete and recreate it with RequiresSession=true. ASB does not allow toggling
  /// RequiresSession on an existing subscription, hence the delete + recreate dance. No-op
  /// otherwise (sessions disabled OR subscription already requires sessions).
  /// </summary>
  private async Task _migrateSubscriptionToSessionsIfNeededAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken) {
    if (!_options.EnableSessions) {
      return;
    }
    var properties = await _adminClient!.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken);
    if (properties.RequiresSession) {
      return;
    }
    if (_logger.IsEnabled(LogLevel.Information)) {
      _logger.LogInformation(
        "Auto-migrating subscription {TopicName}/{SubscriptionName} to require sessions (FIFO ordering). " +
        "Deleting and recreating because ASB does not allow toggling RequiresSession.",
        topicName, subscriptionName);
    }
    await _adminClient.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken);
    await _adminClient.CreateSubscriptionAsync(topicName, subscriptionName, requiresSession: true, _options.MaxDeliveryAttempts, cancellationToken);
  }

  /// <summary>Creates the subscription — honoring EnableSessions for the RequiresSession flag —
  /// and logs at Information when enabled.</summary>
  private async Task _createSubscriptionAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken) {
    if (_logger.IsEnabled(LogLevel.Information)) {
      _logger.LogInformation("Creating subscription {TopicName}/{SubscriptionName}", topicName, subscriptionName);
    }
    if (_options.EnableSessions) {
      await _adminClient!.CreateSubscriptionAsync(topicName, subscriptionName, requiresSession: true, _options.MaxDeliveryAttempts, cancellationToken);
    } else {
      await _adminClient!.CreateSubscriptionAsync(topicName, subscriptionName, _options.MaxDeliveryAttempts, cancellationToken);
    }
  }

  /// <summary>
  /// Applies SqlFilter rules for routing pattern matching.
  /// Translates RabbitMQ-style patterns (e.g., "ns.#") to SQL LIKE patterns.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="routingPatterns">The routing patterns to filter by.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>messaging/transports/azure-service-bus#routing-filters</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_RoutingPatterns_AppliesSqlFilterRuleAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_RoutingPatternWithBareWildcard_TranslatesToPercentAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_RoutingPatternRuleCreationFails_ProceedsWithoutFilterAsync</tests>
  private async Task _applyRoutingPatternFilterAsync(
    string topicName,
    string subscriptionName,
    IEnumerable<string> routingPatterns,
    CancellationToken cancellationToken) {

    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    // Build SQL filter expression
    // "ns1.#,ns2.#" → "sys.Label LIKE 'ns1.%' OR sys.Label LIKE 'ns2.%'"
    // NOTE: Azure Service Bus SqlFilter uses sys.Label for the Subject/Label property,
    // NOT [Subject]. The [Subject] syntax doesn't work for SqlRuleFilter expressions.
    // See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-filter
    var likePatterns = routingPatterns
      .Select(p => p.Replace(".#", ".%").Replace(".*", ".%").Replace("#", "%").Replace("*", "%"))
      .Select(p => $"sys.Label LIKE '{p}'");

    var sqlExpression = string.Join(" OR ", likePatterns);

    const string ruleName = "RoutingPatternFilter";

    try {
      // Idempotency: when the subscription's rules already converge on exactly the desired
      // SqlFilter, leave them alone. Deleting-then-recreating opens a brief no-rule window in
      // which published messages match nothing and are silently dropped — resubscribes (deploy,
      // connection recovery, receive-liveness watchdog) must not reopen that window for a no-op.
      var existingRules = new List<RuleProperties>();
      await foreach (var rule in _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        existingRules.Add(rule);
      }
      if (existingRules.Count == 1
          && existingRules[0].Name == ruleName
          && existingRules[0].Filter is SqlRuleFilter existingFilter
          && existingFilter.SqlExpression == sqlExpression) {
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug(
            "[SqlFilter] Rule already correct on {TopicName}/{SubscriptionName}; skipping re-application",
            topicName,
            subscriptionName);
        }
        return;
      }

      // Delete existing rules (including $Default)
      var deletedRules = new List<string>();
      foreach (var rule in existingRules) {
        await _adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
        deletedRules.Add(rule.Name);
      }

      // Create SqlFilter rule
      var ruleOptions = new CreateRuleOptions(ruleName, new SqlRuleFilter(sqlExpression));
      await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[SqlFilter] Deleted {RuleCount} existing rules and applied SqlFilter '{SqlExpression}' to {TopicName}/{SubscriptionName}",
          deletedRules.Count,
          sqlExpression,
          topicName,
          subscriptionName);
      }
    } catch (Exception ex) {
      _logger.LogWarning(
        ex,
        "Failed to apply routing pattern filter to {TopicName}/{SubscriptionName}. Proceeding without filter.",
        topicName,
        subscriptionName
      );
    }
  }

  #endregion

  /// <summary>
  /// Converts a JsonElement to an AMQP-compatible primitive value.
  /// AMQP application properties only support: string, bool, byte, sbyte, short, ushort,
  /// int, uint, long, ulong, float, double, decimal, Guid, DateTimeOffset, TimeSpan, Uri.
  /// </summary>
  private static object? _convertJsonElementToAmqpValue(JsonElement element) {
    return element.ValueKind switch {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number when element.TryGetInt64(out var longVal) => longVal,
      JsonValueKind.Number when element.TryGetDouble(out var doubleVal) => doubleVal,
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Null => null,
      // For arrays and objects, serialize back to JSON string (AMQP doesn't support complex types)
      JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
      _ => element.ToString()
    };
  }

  /// <summary>
  /// Ensures a topic exists via the admin client, handling race conditions gracefully.
  /// Shared by both subscribe-path and publish-path auto-provisioning.
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#publish-auto-provisioning</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PublishAsync_WithAdminClient_EnsuresTopicExistsAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PublishAsync_WithAdminClient_TopicAlreadyExists_SkipsCreationAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PublishAsync_WithAdminClient_RaceCondition_HandlesGracefullyAsync</tests>
  private async Task _ensureTopicExistsViaAdminAsync(string topicName, CancellationToken cancellationToken) {
    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    try {
      if (!await _adminClient.TopicExistsAsync(topicName, cancellationToken)) {
        await _adminClient.CreateTopicAsync(topicName, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation("Auto-created topic '{TopicName}'", topicName);
        }
      }
    } catch (Azure.RequestFailedException ex) when (ex.Status == 409) {
      // Race condition — another instance created it, safe to ignore
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Topic '{TopicName}' already exists (race condition)", topicName);
      }
    }
  }

  private async Task<ServiceBusSender> _getOrCreateSenderAsync(string topicName, CancellationToken cancellationToken) {
    if (_senders.TryGetValue(topicName, out var existingSender)) {
      return existingSender;
    }

    await _senderLock.WaitAsync(cancellationToken);
    try {
      // Double-check after acquiring lock
      if (_senders.TryGetValue(topicName, out existingSender)) {
        return existingSender;
      }

      // Ensure topic exists before creating sender (on-demand provisioning)
      // This matches RabbitMQ's idempotent ExchangeDeclareAsync behavior
      await _ensureTopicExistsViaAdminAsync(topicName, cancellationToken);

      var sender = _client.CreateSender(topicName);
      _senders[topicName] = sender;

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Created sender for topic {TopicName}", topicName);
      }

      return sender;
    } finally {
      _senderLock.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Clear recovery handler to prevent memory leak
    _recoveryHandler = null;

    // Dispose all senders
    foreach (var sender in _senders.Values) {
      await sender.DisposeAsync();
    }
    _senders.Clear();

    // DON'T dispose _client - it's injected and managed by DI container
    _logger.LogInformation("Transport disposed (client managed by DI)");

    _senderLock.Dispose();

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Routes the ack+drop telemetry through the shared <see cref="IMessageDiscardPolicy"/>
  /// when the reason is <see cref="AsbReceiveReason.NO_LOCAL_CONSUMER"/> — that case logs
  /// at Debug and bumps the <c>whizbang.message.skipped</c> OTel counter. Other AckAndDrop
  /// reasons (genuine envelope/type-resolution failures, deserialization failures) keep
  /// the existing Warning log so they remain visible in production.
  /// </summary>
  /// <remarks>
  /// Internal-static so the unit tests can exercise the level-routing without standing up
  /// a real <c>ServiceBusClient</c>. Caller in the receive path passes
  /// <see cref="_discardPolicy"/> — when the policy isn't wired (legacy / test configs)
  /// the legacy Warning fires so behaviour is never silently lost.
  /// </remarks>
  internal static void EmitAckDropTelemetry(
      ILogger<AzureServiceBusTransport> transportLogger,
      IMessageDiscardPolicy? discardPolicy,
      AsbReceiveDecision decision,
      string? messageId,
      string? sessionId,
      string topic,
      string subscription) {
    if (decision.Reason == AsbReceiveReason.NO_LOCAL_CONSUMER
        && discardPolicy is not null
        && !string.IsNullOrEmpty(decision.EnvelopeTypeName)) {
      var policyDecision = new MessageDiscardDecision(
        ShouldDiscard: true,
        Reason: MessageDiscardReason.NoLocalConsumer,
        Detail: decision.Description);
      discardPolicy.RecordDiscard(
        gate: MessageDiscardGate.Receive,
        decision: policyDecision,
        payloadClrType: decision.EnvelopeTypeName!,
        additionalTags: new Dictionary<string, object?> {
          ["topic"] = topic,
          ["subscription"] = subscription,
          ["message_id"] = messageId,
          ["session_id"] = sessionId,
        });
      return;
    }

    transportLogger.LogWarning(
      "ASB session-receive ack+drop. Reason={Reason} EnvelopeType={EnvelopeType} MessageId={MessageId} SessionId={SessionId} Topic={TopicName} Subscription={SubscriptionName} Detail={Description}",
      decision.Reason,
      decision.EnvelopeTypeName,
      messageId,
      sessionId,
      topic,
      subscription,
      decision.Description);
  }
}
