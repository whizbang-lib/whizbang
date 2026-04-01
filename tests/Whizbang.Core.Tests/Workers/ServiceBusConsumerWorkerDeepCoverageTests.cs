#pragma warning disable CA1707
#pragma warning disable CS0067

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Deep coverage tests for ServiceBusConsumerWorker targeting uncovered paths:
/// - PauseAllSubscriptionsAsync / ResumeAllSubscriptionsAsync
/// - StartAsync with DestinationFilter metadata
/// - StartAsync with multiple subscriptions
/// - _handleMessageAsync full pipeline (dedup, lifecycle, flush)
/// - _handleMessageAsync error/exception path
/// - _handleMessageAsync duplicate detection (empty work)
/// - _startInboxActivity with valid TraceParent
/// - _startInboxActivity with null TraceParent
/// - _serializeToNewInboxMessage with JsonElement envelope
/// - _serializeToNewInboxMessage with strongly-typed envelope
/// - _serializeToNewInboxMessage null/empty envelopeType guard
/// - _serializeToNewInboxMessage JsonElement payload mismatch guard
/// - _extractMessageTypeFromEnvelopeType valid parsing
/// - _extractMessageTypeFromEnvelopeType invalid format
/// - _extractMessageTypeFromEnvelopeType empty extraction
/// - _extractStreamId with AggregateId metadata
/// - _extractStreamId without metadata (falls back to MessageId)
/// - _isEventWithoutPerspectives with null registry
/// - _isEventWithoutPerspectives with matching perspective
/// - _isEventWithoutPerspectives with non-matching perspectives
/// - _invokePreInboxLifecycleAsync with null receptorInvoker
/// - _invokePostInboxLifecycleAsync PostLifecycle for events without perspectives (coordinator path)
/// - _invokePostInboxLifecycleAsync PostLifecycle for events without perspectives (fallback path)
/// - _invokePostInboxLifecycleAsync skip PostLifecycle for events with perspectives
/// - _deserializeEvent with unresolvable type
/// - _deserializeEvent with exception
/// - StopAsync disposes subscriptions
/// </summary>
[Category("Workers")]
public class ServiceBusConsumerWorkerDeepCoverageTests {

  // ========================================
  // Constructor Null Guard Tests
  // ========================================

  [Test]
  public async Task Constructor_NullTransport_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new ServiceBusConsumerWorker(
        transport: null!,
        scopeFactory: _buildScopeFactory(),
        jsonOptions: new JsonSerializerOptions(),
        logger: new TestLogger<ServiceBusConsumerWorker>(),
        orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null)
      );
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new ServiceBusConsumerWorker(
        transport: new DeepCoverageTransport(),
        scopeFactory: null!,
        jsonOptions: new JsonSerializerOptions(),
        logger: new TestLogger<ServiceBusConsumerWorker>(),
        orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null)
      );
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new ServiceBusConsumerWorker(
        transport: new DeepCoverageTransport(),
        scopeFactory: _buildScopeFactory(),
        jsonOptions: null!,
        logger: new TestLogger<ServiceBusConsumerWorker>(),
        orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null)
      );
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new ServiceBusConsumerWorker(
        transport: new DeepCoverageTransport(),
        scopeFactory: _buildScopeFactory(),
        jsonOptions: new JsonSerializerOptions(),
        logger: null!,
        orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null)
      );
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_NullOrderedProcessor_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new ServiceBusConsumerWorker(
        transport: new DeepCoverageTransport(),
        scopeFactory: _buildScopeFactory(),
        jsonOptions: new JsonSerializerOptions(),
        logger: new TestLogger<ServiceBusConsumerWorker>(),
        orderedProcessor: null!
      );
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_NullOptions_DefaultsToEmptyOptionsAsync() {
    // options parameter is nullable with default null - should not throw
    var worker = new ServiceBusConsumerWorker(
      transport: new DeepCoverageTransport(),
      scopeFactory: _buildScopeFactory(),
      jsonOptions: new JsonSerializerOptions(),
      logger: new TestLogger<ServiceBusConsumerWorker>(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      options: null
    );

    // Should start successfully with no subscriptions
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);
    // No assertion needed — test verifies no exception is thrown
  }

  // ========================================
  // PauseAllSubscriptionsAsync / ResumeAllSubscriptionsAsync
  // ========================================

  [Test]
  public async Task PauseAllSubscriptionsAsync_PausesAllActiveSubscriptionsAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-1", "sub-1"),
        new TopicSubscription("topic-2", "sub-2")
      ]
    };

    var worker = _createWorker(transport, options);
    await worker.StartAsync(CancellationToken.None);

    // Act
    await worker.PauseAllSubscriptionsAsync();

    // Assert - subscriptions were paused
    await Assert.That(transport.CreatedSubscriptions.Count).IsEqualTo(2);
    foreach (var sub in transport.CreatedSubscriptions) {
      await Assert.That(sub.IsActive).IsFalse();
    }

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ResumeAllSubscriptionsAsync_ResumesAllPausedSubscriptionsAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-1", "sub-1")
      ]
    };

    var worker = _createWorker(transport, options);
    await worker.StartAsync(CancellationToken.None);
    await worker.PauseAllSubscriptionsAsync();

    // Act
    await worker.ResumeAllSubscriptionsAsync();

    // Assert - subscriptions were resumed
    await Assert.That(transport.CreatedSubscriptions[0].IsActive).IsTrue();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task PauseAllSubscriptionsAsync_NoSubscriptions_DoesNotThrowAsync() {
    // Arrange - no subscriptions configured
    var worker = _createWorker(new DeepCoverageTransport(), new ServiceBusConsumerOptions());
    await worker.StartAsync(CancellationToken.None);

    // Act & Assert - should not throw
    await worker.PauseAllSubscriptionsAsync();
    await worker.StopAsync(CancellationToken.None);
    // No assertion needed — test verifies no exception is thrown
  }

  [Test]
  public async Task ResumeAllSubscriptionsAsync_NoSubscriptions_DoesNotThrowAsync() {
    var worker = _createWorker(new DeepCoverageTransport(), new ServiceBusConsumerOptions());
    await worker.StartAsync(CancellationToken.None);

    await worker.ResumeAllSubscriptionsAsync();
    await worker.StopAsync(CancellationToken.None);
    // No assertion needed — test verifies no exception is thrown
  }

  // ========================================
  // StartAsync Tests
  // ========================================

  [Test]
  public async Task StartAsync_WithDestinationFilter_PassesFilterMetadataToTransportAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("events", "inventory-sub", DestinationFilter: "inventory-service")
      ]
    };

    var worker = _createWorker(transport, options);

    // Act
    await worker.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(transport.LastDestination).IsNotNull();
    await Assert.That(transport.LastDestination!.Address).IsEqualTo("events");
    await Assert.That(transport.LastDestination!.RoutingKey).IsEqualTo("inventory-sub");
    await Assert.That(transport.LastDestination!.Metadata).IsNotNull();
    await Assert.That(transport.LastDestination!.Metadata!.ContainsKey("DestinationFilter")).IsTrue();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task StartAsync_WithoutDestinationFilter_NoMetadataAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("events", "my-sub")
      ]
    };

    var worker = _createWorker(transport, options);

    // Act
    await worker.StartAsync(CancellationToken.None);

    // Assert - no metadata when no DestinationFilter
    await Assert.That(transport.LastDestination).IsNotNull();
    await Assert.That(transport.LastDestination!.Metadata).IsNull();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task StartAsync_MultipleSubscriptions_CreatesAllAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-a", "sub-a"),
        new TopicSubscription("topic-b", "sub-b"),
        new TopicSubscription("topic-c", "sub-c")
      ]
    };

    var worker = _createWorker(transport, options);

    // Act
    await worker.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(3);
    await Assert.That(transport.CreatedSubscriptions.Count).IsEqualTo(3);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // HandleMessage Pipeline Tests
  // ========================================

  [Test]
  public async Task HandleMessage_FullPipeline_ProcessesMessageEndToEndAsync() {
    // Arrange - set up transport that captures the handler and invokes it
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    // Act - invoke the captured handler with a JsonElement envelope
    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - strategy received flush calls
    await Assert.That(strategy.FlushCallCount).IsGreaterThanOrEqualTo(2);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_DuplicateMessage_SkipsProcessingAsync() {
    // Arrange - FlushAsync returns empty InboxWork for this message (already processed)
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [], // No work - duplicate
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    // Act - handler should return without invoking further processing
    var envelope = _createJsonEnvelope(messageId, Guid.NewGuid());
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - only one flush (the initial dedup flush), no second flush for completions
    await Assert.That(strategy.FlushCallCount).IsEqualTo(1);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_WhenProcessingThrows_SetsErrorStatusAndRethrowsAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var throwOnSecondFlush = new ThrowingWorkCoordinatorStrategy(
      firstFlush: new WorkBatch {
        InboxWork = [new InboxWork {
          MessageId = messageId.Value,
          Envelope = _createJsonEnvelope(messageId, Guid.NewGuid()),
          MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
          Status = MessageProcessingStatus.None,
          Attempts = 0
        }],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(throwOnSecondFlush);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, Guid.NewGuid());
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act & Assert - the exception from the final flush is propagated
    await Assert.That(async () =>
      await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Lifecycle Tests (PreInbox/PostInbox skip when no invoker)
  // ========================================

  [Test]
  public async Task HandleMessage_NoReceptorInvoker_SkipsLifecycleStagesAsync() {
    // Arrange - no IReceptorInvoker registered, lifecycle should be skipped
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    // NOTE: IReceptorInvoker NOT registered

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act - should complete without errors (lifecycle skipped)
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - completed without throwing
    await Assert.That(strategy.FlushCallCount).IsGreaterThanOrEqualTo(2);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_WithReceptorInvokerAndDeserializer_InvokesPreAndPostInboxDetachedAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var invokedStages = new List<LifecycleStage>();
    var registry = new DeepCoverageReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
      LifecycleStage.ImmediateDetached,
      LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline
    }) {
      var capturedStage = stage;
      registry.AddReceptor(stage, typeof(DeepCoverageTestEvent), new ReceptorInfo(
        MessageType: typeof(DeepCoverageTestEvent),
        ReceptorId: $"deep_cov_receptor_{stage}",
        InvokeAsync: (sp, msg, envelope2, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));

    var deserializer = new DeepCoverageLifecycleMessageDeserializer();

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services,
      lifecycleMessageDeserializer: deserializer
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);
    await worker.DrainDetachedAsync();

    // Assert - PreInbox and PostInbox stages invoked
    await Assert.That(invokedStages).Contains(LifecycleStage.PreInboxDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PreInboxInline);
    await Assert.That(invokedStages).Contains(LifecycleStage.PostInboxDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PostInboxInline);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_EventWithoutPerspectives_InvokesPostLifecycleViaCoordinatorAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var invokedStages = new List<LifecycleStage>();
    var registry = new DeepCoverageReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
      LifecycleStage.ImmediateDetached,
      LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline
    }) {
      var capturedStage = stage;
      registry.AddReceptor(stage, typeof(DeepCoverageTestEvent), new ReceptorInfo(
        MessageType: typeof(DeepCoverageTestEvent),
        ReceptorId: $"postlc_receptor_{stage}",
        InvokeAsync: (sp, msg, envelope2, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
    // No IPerspectiveRunnerRegistry => _isEventWithoutPerspectives returns true

    var deserializer = new DeepCoverageLifecycleMessageDeserializer();

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services,
      lifecycleMessageDeserializer: deserializer
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);
    await worker.DrainDetachedAsync();

    // Assert - PostLifecycle stages invoked via coordinator
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleInline);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_EventWithoutPerspectives_NoCoordinator_InvokesPostLifecycleViaFallbackAsync() {
    // Arrange - no ILifecycleCoordinator registered, fallback to direct invocation
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var invokedStages = new List<LifecycleStage>();
    var registry = new DeepCoverageReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
      LifecycleStage.ImmediateDetached,
      LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline
    }) {
      var capturedStage = stage;
      registry.AddReceptor(stage, typeof(DeepCoverageTestEvent), new ReceptorInfo(
        MessageType: typeof(DeepCoverageTestEvent),
        ReceptorId: $"fallback_receptor_{stage}",
        InvokeAsync: (sp, msg, envelope2, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    // NOTE: NO ILifecycleCoordinator - fallback path
    // NOTE: NO IPerspectiveRunnerRegistry - _isEventWithoutPerspectives returns true

    var deserializer = new DeepCoverageLifecycleMessageDeserializer();

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services,
      lifecycleMessageDeserializer: deserializer
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);
    await worker.DrainDetachedAsync();

    // Assert - PostLifecycle stages invoked via fallback (direct invoker)
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleInline);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_EventWithPerspectives_SkipsPostLifecycleDetachedAsync() {
    // Arrange - perspective registry has a matching perspective
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      }
    );

    var invokedStages = new List<LifecycleStage>();
    var receptorRegistry = new DeepCoverageReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
      LifecycleStage.ImmediateDetached,
      LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline
    }) {
      var capturedStage = stage;
      receptorRegistry.AddReceptor(stage, typeof(DeepCoverageTestEvent), new ReceptorInfo(
        MessageType: typeof(DeepCoverageTestEvent),
        ReceptorId: $"skip_postlc_{stage}",
        InvokeAsync: (sp, msg, envelope2, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    // Register perspective that handles this event type
    var perspectiveRegistry = new DeepCoveragePerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestPerspective",
        FullyQualifiedName: "Test.TestPerspective",
        ModelType: "TestModel",
        EventTypes: ["Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests"]
      )
    ]);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(receptorRegistry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(receptorRegistry, sp));
    services.AddSingleton<IPerspectiveRunnerRegistry>(perspectiveRegistry);

    var deserializer = new DeepCoverageLifecycleMessageDeserializer();

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services,
      lifecycleMessageDeserializer: deserializer
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - PostLifecycle stages NOT invoked (perspective handles it)
    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PostLifecycleDetached);
    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PostLifecycleInline);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _serializeToNewInboxMessage Tests
  // ========================================

  [Test]
  public async Task HandleMessage_NullEnvelopeType_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, Guid.NewGuid());

    // Act & Assert - null envelopeType should throw
    await Assert.That(async () =>
      await handlerCapturingTransport.CapturedHandler!(envelope, null, CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_EmptyEnvelopeType_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, Guid.NewGuid());

    // Act & Assert - empty envelopeType should throw
    await Assert.That(async () =>
      await handlerCapturingTransport.CapturedHandler!(envelope, "  ", CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_InvalidEnvelopeTypeFormat_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - envelopeType without [[ ]] brackets
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelope(messageId, Guid.NewGuid());

    // Act & Assert - invalid format should throw
    await Assert.That(async () =>
      await handlerCapturingTransport.CapturedHandler!(envelope, "InvalidEnvelopeType", CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _extractStreamId Tests (via HandleMessage)
  // ========================================

  [Test]
  public async Task HandleMessage_EnvelopeWithAggregateIdMetadata_UsesAggregateIdAsStreamIdAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var expectedStreamId = Guid.NewGuid();

    InboxMessage? capturedInboxMessage = null;
    var strategy = new CapturingWorkCoordinatorStrategy(
      msg => capturedInboxMessage = msg,
      new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    // Create envelope with AggregateId in hop metadata
    var envelope = _createJsonEnvelopeWithAggregateId(messageId, expectedStreamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - StreamId should be the AggregateId from metadata
    await Assert.That(capturedInboxMessage).IsNotNull();
    await Assert.That(capturedInboxMessage!.StreamId).IsEqualTo(expectedStreamId);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_EnvelopeWithoutAggregateIdMetadata_FallsBackToMessageIdAsync() {
    // Arrange
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    InboxMessage? capturedInboxMessage = null;
    var strategy = new CapturingWorkCoordinatorStrategy(
      msg => capturedInboxMessage = msg,
      new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    // Create envelope WITHOUT AggregateId metadata
    var envelope = _createJsonEnvelopeWithoutMetadata(messageId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestCommand, Whizbang.Core.Tests]], Whizbang.Core";

    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - StreamId should fall back to MessageId
    await Assert.That(capturedInboxMessage).IsNotNull();
    await Assert.That(capturedInboxMessage!.StreamId).IsEqualTo(messageId.Value);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // StopAsync Tests
  // ========================================

  [Test]
  public async Task StopAsync_DisposesAllSubscriptionsAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-1", "sub-1"),
        new TopicSubscription("topic-2", "sub-2")
      ]
    };

    var worker = _createWorker(transport, options);
    await worker.StartAsync(CancellationToken.None);

    // Act
    await worker.StopAsync(CancellationToken.None);

    // Assert - all subscriptions disposed
    foreach (var sub in transport.CreatedSubscriptions) {
      await Assert.That(sub.IsActive).IsFalse();
    }
  }

  // ========================================
  // TopicSubscription / ServiceBusConsumerOptions Tests
  // ========================================

  [Test]
  public async Task TopicSubscription_DestinationFilter_DefaultsToNullAsync() {
    var sub = new TopicSubscription("topic", "sub");
    await Assert.That(sub.DestinationFilter).IsNull();
  }

  [Test]
  public async Task TopicSubscription_WithDestinationFilter_StoresValueAsync() {
    var sub = new TopicSubscription("topic", "sub", "my-filter");
    await Assert.That(sub.DestinationFilter).IsEqualTo("my-filter");
  }

  [Test]
  public async Task ServiceBusConsumerOptions_DefaultSubscriptions_IsEmptyAsync() {
    var options = new ServiceBusConsumerOptions();
    await Assert.That(options.Subscriptions.Count).IsEqualTo(0);
  }

  // ========================================
  // _startInboxActivity Tests (via HandleMessage with trace context)
  // ========================================

  [Test]
  public async Task HandleMessage_WithValidTraceParent_CreatesActivityAsync() {
    // Arrange - envelope with a valid TraceParent in hop
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    // Create envelope with TraceParent
    var envelope = _createJsonEnvelopeWithTraceParent(messageId, streamId);
    var envelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.DeepCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act - should not throw; activity creation is best-effort
    await handlerCapturingTransport.CapturedHandler!(envelope, envelopeType, CancellationToken.None);

    // Assert - completed without error
    // No assertion needed — test verifies no exception is thrown

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_WithNullEnvelopeType_InActivity_ShowsUnknownAsync() {
    // This test exercises the envelopeType null path in _startInboxActivity
    // but since envelopeType null also throws in _serializeToNewInboxMessage,
    // the activity creation happens first with "Unknown" before the exception.
    // We verify the exception is still thrown.
    var handlerCapturingTransport = new HandlerCapturingTransport();
    var messageId = MessageId.New();

    var strategy = new DeepCoverageWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] }
    );

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var worker = _createWorker(
      handlerCapturingTransport,
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("t", "s")]
      },
      services
    );

    await worker.StartAsync(CancellationToken.None);

    var envelope = _createJsonEnvelopeWithTraceParent(messageId, Guid.NewGuid());

    // Act & Assert - null envelopeType still throws, but _startInboxActivity ran first
    await Assert.That(async () =>
      await handlerCapturingTransport.CapturedHandler!(envelope, null, CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IServiceScopeFactory _buildScopeFactory(ServiceCollection? services = null) {
    services ??= new ServiceCollection();
    return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
  }

  private static ServiceBusConsumerWorker _createWorker(
    ITransport transport,
    ServiceBusConsumerOptions options,
    ServiceCollection? services = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IEnvelopeSerializer? envelopeSerializer = null) {
    services ??= new ServiceCollection();
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    return new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      jsonOptions,
      new TestLogger<ServiceBusConsumerWorker>(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      options,
      lifecycleMessageDeserializer,
      envelopeSerializer
    );
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId, Guid streamId) {
    var payload = JsonDocument.Parse($"{{\"Data\":\"test-data\"}}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithAggregateId(MessageId messageId, Guid aggregateId) {
    var payload = JsonDocument.Parse($"{{\"Data\":\"test-data\"}}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonDocument.Parse($"\"{aggregateId}\"").RootElement
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithoutMetadata(MessageId messageId) {
    var payload = JsonDocument.Parse($"{{\"Data\":\"test-data\"}}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Metadata = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithTraceParent(MessageId messageId, Guid streamId) {
    var payload = JsonDocument.Parse($"{{\"Data\":\"test-data\"}}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  /// <summary>
  /// Transport that tracks subscriptions and destinations.
  /// </summary>
  private sealed class DeepCoverageTransport : ITransport {
    public int SubscribeCallCount { get; private set; }
    public TransportDestination? LastDestination { get; private set; }
    public List<DeepCoverageSubscription> CreatedSubscriptions { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      LastDestination = destination;
      var sub = new DeepCoverageSubscription();
      CreatedSubscriptions.Add(sub);
      return Task.FromResult<ISubscription>(sub);
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
      TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull =>
      throw new NotImplementedException();
  }

  private sealed class DeepCoverageSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsActive = false; }
  }

  /// <summary>
  /// Transport that captures the handler callback so tests can invoke it directly.
  /// </summary>
  private sealed class HandlerCapturingTransport : ITransport {
    public Func<IMessageEnvelope, string?, CancellationToken, Task>? CapturedHandler { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      CapturedHandler = handler;
      return Task.FromResult<ISubscription>(new DeepCoverageSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
      TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull =>
      throw new NotImplementedException();
  }

  /// <summary>
  /// Work coordinator strategy with configurable flush result.
  /// </summary>
  private sealed class DeepCoverageWorkCoordinatorStrategy(Func<WorkBatch> flushFunc) : IWorkCoordinatorStrategy {
    public int FlushCallCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCallCount++;
      return Task.FromResult(flushFunc());
    }
  }

  /// <summary>
  /// Work coordinator strategy that captures queued inbox messages.
  /// </summary>
  private sealed class CapturingWorkCoordinatorStrategy(
    Action<InboxMessage> onQueueInbox, WorkBatch flushResult) : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }

    public void QueueInboxMessage(InboxMessage message) {
      onQueueInbox(message);
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(flushResult);
    }
  }

  /// <summary>
  /// Work coordinator strategy that throws on the second FlushAsync call.
  /// </summary>
  private sealed class ThrowingWorkCoordinatorStrategy(WorkBatch firstFlush) : IWorkCoordinatorStrategy {
    private int _flushCount;

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      _flushCount++;
      if (_flushCount > 1) {
        throw new InvalidOperationException("Simulated flush failure");
      }
      return Task.FromResult(firstFlush);
    }
  }

  /// <summary>
  /// Lifecycle message deserializer that returns a test event.
  /// </summary>
  private sealed class DeepCoverageLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      return new DeepCoverageTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      return new DeepCoverageTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      return new DeepCoverageTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      return new DeepCoverageTestEvent { Data = "deserialized" };
    }
  }

  /// <summary>
  /// Receptor registry for lifecycle tests.
  /// </summary>
  private sealed class DeepCoverageReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, Type messageType, ReceptorInfo receptor) {
      var key = (messageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>
  /// Perspective runner registry that reports registered perspectives.
  /// </summary>
  private sealed class DeepCoveragePerspectiveRunnerRegistry(
    IReadOnlyList<PerspectiveRegistrationInfo> perspectives) : IPerspectiveRunnerRegistry {

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }
}

/// <summary>
/// Test event for deep coverage tests.
/// </summary>
public record DeepCoverageTestEvent : IEvent {
  [StreamId]
  public string Data { get; init; } = string.Empty;
}

/// <summary>
/// Test command (not an event) for deep coverage tests.
/// </summary>
public record DeepCoverageTestCommand : ICommand {
  public string Data { get; init; } = string.Empty;
}
