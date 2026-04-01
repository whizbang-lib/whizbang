using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for security context propagation to CASCADED events.
/// When a command handler returns events and Whizbang cascades them,
/// the cascaded events should inherit SecurityContext (TenantId, UserId)
/// from the parent command's scope context.
/// </summary>
/// <docs>core-concepts/message-security#automatic-security-propagation</docs>
/// <remarks>
/// This is critical for lifecycle receptors (PostPerspectiveDetached) that need
/// access to TenantId from the original dispatch context.
/// </remarks>
[Category("Security")]
[Category("Dispatcher")]
[Category("Cascade")]
[NotInParallel]
public class DispatcherCascadeSecurityPropagationTests {
  // ============================================
  // Test Messages and Events
  // ============================================

  /// <summary>
  /// Command that when handled returns events for cascading.
  /// </summary>
  public record CascadeTestCommand(string Data, Guid StreamId);

  /// <summary>
  /// Event returned from command handler - will be cascaded.
  /// Uses [DefaultRouting(Outbox)] to go through outbox storage path.
  /// </summary>
  [DefaultRouting(DispatchModes.Outbox)]
  public record CascadeTestEvent([property: StreamId] Guid StreamId, string ProcessedData) : IEvent;

  /// <summary>
  /// Result DTO returned from handler.
  /// </summary>
  public record CascadeTestResult(string Processed);

  // ============================================
  // Test Infrastructure
  // ============================================

  /// <summary>
  /// Tracks security context seen during cascade.
  /// </summary>
  public static class CascadeSecurityTracker {
    public static IScopeContext? CapturedScopeContext { get; private set; }
    public static bool WasCalled { get; private set; }

    public static void Reset() {
      CapturedScopeContext = null;
      WasCalled = false;
    }

    public static void Capture(IScopeContext? context) {
      CapturedScopeContext = context;
      WasCalled = true;
    }
  }

  /// <summary>
  /// Command handler that returns an event to be cascaded.
  /// Captures the scope context during execution.
  /// </summary>
  public class CascadeTestCommandReceptor(IScopeContextAccessor scopeContextAccessor) : IReceptor<CascadeTestCommand, (CascadeTestResult, CascadeTestEvent)> {
    private readonly IScopeContextAccessor _scopeContextAccessor = scopeContextAccessor;

    public ValueTask<(CascadeTestResult, CascadeTestEvent)> HandleAsync(
      CascadeTestCommand message,
      CancellationToken cancellationToken = default) {
      // Capture the scope context during handler execution
      CascadeSecurityTracker.Capture(_scopeContextAccessor.Current);

      var result = new CascadeTestResult($"Processed: {message.Data}");
      var evt = new CascadeTestEvent(message.StreamId, message.Data);
      return ValueTask.FromResult((result, evt));
    }
  }

  // ============================================
  // Tests
  // ============================================

  /// <summary>
  /// When a command is dispatched with WithTenant() and the handler returns events,
  /// the cascaded events should have SecurityContext.TenantId in their envelope hops.
  /// This is critical for PostPerspectiveDetached handlers that need TenantId.
  /// </summary>
  [Test]
  public async Task WithTenant_CascadedEvents_HaveTenantIdInSecurityContextAsync() {
    // Arrange
    CascadeSecurityTracker.Reset();
    var scopeContextAccessor = new ScopeContextAccessor();
    var outboxCapture = new OutboxMessageCapture();
    var (dispatcher, _) = _createDispatcherWithOutboxCapture(scopeContextAccessor, outboxCapture);

    var command = new CascadeTestCommand("test-data", Guid.NewGuid());

    // Act - Dispatch with explicit tenant
    await dispatcher.AsSystem().ForTenant("target-tenant-123").SendAsync(command);

    // Assert - Handler should have seen the scope context with TenantId
    await Assert.That(CascadeSecurityTracker.WasCalled).IsTrue();
    await Assert.That(CascadeSecurityTracker.CapturedScopeContext).IsNotNull();
    await Assert.That(CascadeSecurityTracker.CapturedScopeContext!.Scope.TenantId)
      .IsEqualTo("target-tenant-123");

    // Assert - The cascaded event should have TenantId in the envelope's hop Scope
    await Assert.That(outboxCapture.CapturedMessages).Count().IsGreaterThanOrEqualTo(1);

    var outboxMsg = outboxCapture.CapturedMessages[0];
    await Assert.That(outboxMsg.Metadata).IsNotNull();
    await Assert.That(outboxMsg.Metadata.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = outboxMsg.Metadata.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var firstHopScope = hop.Scope?.ApplyTo(null);
    await Assert.That(firstHopScope?.Scope?.TenantId).IsEqualTo("target-tenant-123");
  }

  /// <summary>
  /// When a command is dispatched with WithTenant() and RunAs(),
  /// cascaded events should have both TenantId and UserId.
  /// </summary>
  [Test]
  public async Task WithTenantAndRunAs_CascadedEvents_HaveBothTenantAndUserIdAsync() {
    // Arrange
    CascadeSecurityTracker.Reset();
    var scopeContextAccessor = new ScopeContextAccessor();
    var outboxCapture = new OutboxMessageCapture();
    var (dispatcher, _) = _createDispatcherWithOutboxCapture(scopeContextAccessor, outboxCapture);

    var command = new CascadeTestCommand("test-data", Guid.NewGuid());

    // Act - Dispatch with tenant and user
    await dispatcher.RunAs("user@example.com").ForTenant("tenant-456").SendAsync(command);

    // Assert - Handler should have seen the scope context
    await Assert.That(CascadeSecurityTracker.WasCalled).IsTrue();
    await Assert.That(CascadeSecurityTracker.CapturedScopeContext).IsNotNull();
    await Assert.That(CascadeSecurityTracker.CapturedScopeContext!.Scope.TenantId)
      .IsEqualTo("tenant-456");
    await Assert.That(CascadeSecurityTracker.CapturedScopeContext!.Scope.UserId)
      .IsEqualTo("user@example.com");

    // Assert - The cascaded event should have both TenantId and UserId in Scope
    await Assert.That(outboxCapture.CapturedMessages).Count().IsGreaterThanOrEqualTo(1);

    var outboxMsg = outboxCapture.CapturedMessages[0];
    await Assert.That(outboxMsg.Metadata).IsNotNull();

    var hop = outboxMsg.Metadata.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var firstHopScope = hop.Scope?.ApplyTo(null);
    await Assert.That(firstHopScope?.Scope?.TenantId).IsEqualTo("tenant-456");
    await Assert.That(firstHopScope?.Scope?.UserId).IsEqualTo("user@example.com");
  }

  /// <summary>
  /// When a command is dispatched WITHOUT explicit security context,
  /// cascaded events should still work (no security context).
  /// </summary>
  [Test]
  public async Task NoSecurityContext_CascadedEvents_HaveNullScopeAsync() {
    // Arrange
    CascadeSecurityTracker.Reset();
    var scopeContextAccessor = new ScopeContextAccessor {
      Current = null // No security context
    };
    var outboxCapture = new OutboxMessageCapture();
    var (dispatcher, _) = _createDispatcherWithOutboxCapture(scopeContextAccessor, outboxCapture);

    var command = new CascadeTestCommand("test-data", Guid.NewGuid());

    // Act - Dispatch without explicit security
    await dispatcher.SendAsync(command);

    // Assert - Handler ran
    await Assert.That(CascadeSecurityTracker.WasCalled).IsTrue();

    // Assert - The cascaded event should have null Scope
    await Assert.That(outboxCapture.CapturedMessages).Count().IsGreaterThanOrEqualTo(1);

    var outboxMsg = outboxCapture.CapturedMessages[0];
    await Assert.That(outboxMsg.Metadata).IsNotNull();

    var hop = outboxMsg.Metadata.Hops[0];
    // Scope should be null when no context was set
    await Assert.That(hop.Scope).IsNull();
  }

  /// <summary>
  /// When using ambient scope context (not DispatcherSecurityBuilder),
  /// cascaded events should still inherit the Scope.
  /// </summary>
  [Test]
  public async Task AmbientScopeContext_CascadedEvents_InheritScopeAsync() {
    // Arrange
    CascadeSecurityTracker.Reset();
    var scopeContextAccessor = new ScopeContextAccessor();
    var outboxCapture = new OutboxMessageCapture();
    var (dispatcher, _) = _createDispatcherWithOutboxCapture(scopeContextAccessor, outboxCapture);

    // Set up ambient context (as if middleware established it)
    var scope = new PerspectiveScope {
      UserId = "ambient-user",
      TenantId = "ambient-tenant"
    };
    var extraction = _createExtraction(scope);
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    var command = new CascadeTestCommand("test-data", Guid.NewGuid());

    // Act - Dispatch using ambient context
    await dispatcher.SendAsync(command);

    // Assert - The cascaded event should have Scope from ambient
    await Assert.That(outboxCapture.CapturedMessages).Count().IsGreaterThanOrEqualTo(1);

    var outboxMsg = outboxCapture.CapturedMessages[0];
    var hop = outboxMsg.Metadata.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var firstHopScope = hop.Scope?.ApplyTo(null);
    await Assert.That(firstHopScope?.Scope?.TenantId).IsEqualTo("ambient-tenant");
    await Assert.That(firstHopScope?.Scope?.UserId).IsEqualTo("ambient-user");
  }

  // ============================================
  // Helper Classes
  // ============================================

  /// <summary>
  /// Captures outbox messages for test verification.
  /// Implements IWorkCoordinatorStrategy to intercept cascade operations.
  /// </summary>
  public class OutboxMessageCapture : IWorkCoordinatorStrategy {
    private readonly List<OutboxMessage> _capturedMessages = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<OutboxMessage> CapturedMessages {
      get {
        lock (_lock) {
          return [.. _capturedMessages];
        }
      }
    }

    public void QueueOutboxMessage(OutboxMessage message) {
      lock (_lock) {
        _capturedMessages.Add(message);
      }
    }

    public void QueueInboxMessage(InboxMessage message) {
      // Not needed for these tests
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
      // Not needed for these tests
    }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
      // Not needed for these tests
    }

    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
      // Not needed for these tests
    }

    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
      // Not needed for these tests
    }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      // Return empty work batch - we just want to capture the messages
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }

  // ============================================
  // Helper Methods
  // ============================================

  private static SecurityExtraction _createExtraction(PerspectiveScope scope) {
    return new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
  }

  private static JsonSerializerOptions _createTestJsonOptions() {
    return new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = false,
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Whizbang.Core.Generated.WhizbangIdJsonContext.Default,  // Custom converters for MessageId/CorrelationId
        CascadeTestJsonContext.Default,  // Test message types
        InfrastructureJsonContext.Default
      )
    };
  }

  private static (IDispatcher dispatcher, IServiceProvider provider) _createDispatcherWithOutboxCapture(
    IScopeContextAccessor scopeContextAccessor,
    IWorkCoordinatorStrategy outboxCapture) {

    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddSingleton(scopeContextAccessor);

    // Register the outbox capture as the work coordinator strategy
    // This allows us to intercept cascaded events before they hit the database
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outboxCapture);

    // Register JsonSerializerOptions with test types
    var jsonOptions = _createTestJsonOptions();
    services.AddSingleton(jsonOptions);

    // Register IEnvelopeSerializer with proper JSON options
    services.AddSingleton<IEnvelopeSerializer>(sp => {
      var options = sp.GetRequiredService<JsonSerializerOptions>();
      return new EnvelopeSerializer(options);
    });

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return (serviceProvider.GetRequiredService<IDispatcher>(), serviceProvider);
  }
}

/// <summary>
/// JSON context for cascade test message types.
/// </summary>
[JsonSerializable(typeof(DispatcherCascadeSecurityPropagationTests.CascadeTestCommand))]
[JsonSerializable(typeof(DispatcherCascadeSecurityPropagationTests.CascadeTestEvent))]
[JsonSerializable(typeof(DispatcherCascadeSecurityPropagationTests.CascadeTestResult))]
[JsonSerializable(typeof(MessageEnvelope<DispatcherCascadeSecurityPropagationTests.CascadeTestCommand>))]
[JsonSerializable(typeof(MessageEnvelope<DispatcherCascadeSecurityPropagationTests.CascadeTestEvent>))]
[JsonSerializable(typeof(MessageEnvelope<JsonElement>))]
[JsonSerializable(typeof(object))]
internal sealed partial class CascadeTestJsonContext : JsonSerializerContext {
}
