using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for null envelope cascade paths where EstablishMessageContextForCascade() is called.
/// When cascade paths don't have a source envelope (e.g., return value cascades),
/// the generated dispatcher should call EstablishMessageContextForCascade() in the else branch.
/// </summary>
/// <docs>core-concepts/dispatcher#null-envelope-cascade-paths</docs>
/// <docs>core-concepts/message-security#asynclocal-context-flow</docs>
/// <remarks>
/// These tests verify that the generated dispatcher's else branch (when sourceEnvelope is null)
/// correctly establishes message context from AsyncLocal via EstablishMessageContextForCascade().
/// This enables UserContextManager and other services to access UserId/TenantId in cascaded receptors.
/// </remarks>
[Category("Security")]
[Category("Dispatcher")]
[Category("Cascade")]
[NotInParallel]
public class DispatcherCascadeNullEnvelopeTests {
  // ============================================
  // Test Messages and Events
  // ============================================

  public record NullEnvelopeCommand(string Data, Guid StreamId);
  public record NullEnvelopeResult(string Processed);

  [DefaultRouting(DispatchMode.Local)]
  public record NullEnvelopeEvent([property: StreamId] Guid StreamId, string ProcessedData) : IEvent;

  // ============================================
  // Test Infrastructure
  // ============================================

  public static class NullEnvelopeCascadeTracker {
    private static readonly List<(string? userId, string? tenantId)> _capturedContexts = [];
    private static readonly Lock _lock = new();

    public static void Reset() {
      lock (_lock) {
        _capturedContexts.Clear();
      }
    }

    public static void Track(string? userId, string? tenantId) {
      lock (_lock) {
        _capturedContexts.Add((userId, tenantId));
      }
    }

    public static IReadOnlyList<(string? userId, string? tenantId)> GetCapturedContexts() {
      lock (_lock) {
        return [.. _capturedContexts];
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _capturedContexts.Count;
        }
      }
    }
  }

  // ============================================
  // Test Receptors
  // ============================================

  public class NullEnvelopeCommandReceptor : IReceptor<NullEnvelopeCommand, (NullEnvelopeResult, NullEnvelopeEvent)> {
    public ValueTask<(NullEnvelopeResult, NullEnvelopeEvent)> HandleAsync(
      NullEnvelopeCommand message,
      CancellationToken cancellationToken = default) {
      var result = new NullEnvelopeResult($"Processed: {message.Data}");
      var evt = new NullEnvelopeEvent(message.StreamId, message.Data);
      return ValueTask.FromResult((result, evt));
    }
  }

  public class NullEnvelopeEventReceptor(IMessageContextAccessor messageContextAccessor) : IReceptor<NullEnvelopeEvent> {
    private readonly IMessageContextAccessor _messageContextAccessor = messageContextAccessor;

    public ValueTask HandleAsync(NullEnvelopeEvent message, CancellationToken cancellationToken = default) {
      // Capture the message context that should have been established by EstablishMessageContextForCascade
      var context = _messageContextAccessor.Current;
      NullEnvelopeCascadeTracker.Track(context?.UserId, context?.TenantId);
      return ValueTask.CompletedTask;
    }
  }

  // ============================================
  // Tests
  // ============================================

  /// <summary>
  /// When cascade path has null envelope, the else branch should be executed.
  /// Event receptor should be invoked without throwing.
  /// </summary>
  [Test]
  public async Task Cascade_WithNullEnvelope_DoesNotThrowAsync() {
    // Arrange
    NullEnvelopeCascadeTracker.Reset();
    var (dispatcher, _) = _createDispatcher();
    var command = new NullEnvelopeCommand("test-data", Guid.NewGuid());

    // Act & Assert - Should not throw
    await dispatcher.SendAsync(command);

    // Assert - Event receptor should have been called (proves cascade worked)
    await Assert.That(NullEnvelopeCascadeTracker.Count).IsEqualTo(1);
  }

  /// <summary>
  /// When cascade has null envelope and UserId is set, event receptor should see it.
  /// Proves EstablishMessageContextForCascade() reads from AsyncLocal and sets MessageContext.
  /// </summary>
  [Test]
  public async Task Cascade_WithNullEnvelope_PropagatesUserIdAsync() {
    // Arrange
    NullEnvelopeCascadeTracker.Reset();
    var (dispatcher, _) = _createDispatcher();
    var command = new NullEnvelopeCommand("test-data", Guid.NewGuid());

    // Act - Dispatch with RunAs to set UserId in parent scope
    await dispatcher.RunAs("user-123").ForAllTenants().SendAsync(command);

    // Assert - Event receptor should see UserId from parent scope
    await Assert.That(NullEnvelopeCascadeTracker.Count).IsEqualTo(1);
    var contexts = NullEnvelopeCascadeTracker.GetCapturedContexts();
    await Assert.That(contexts[0].userId).IsEqualTo("user-123");
  }

  /// <summary>
  /// When cascade has null envelope and TenantId is set, event receptor should see it.
  /// Proves AsyncLocal context flow works correctly.
  /// </summary>
  [Test]
  public async Task Cascade_WithNullEnvelope_PropagatesTenantIdAsync() {
    // Arrange
    NullEnvelopeCascadeTracker.Reset();
    var (dispatcher, _) = _createDispatcher();
    var command = new NullEnvelopeCommand("test-data", Guid.NewGuid());

    // Act - Dispatch with WithTenant to set TenantId in parent scope
    await dispatcher.AsSystem().ForTenant("tenant-456").SendAsync(command);

    // Assert - Event receptor should see TenantId from parent scope
    await Assert.That(NullEnvelopeCascadeTracker.Count).IsEqualTo(1);
    var contexts = NullEnvelopeCascadeTracker.GetCapturedContexts();
    await Assert.That(contexts[0].tenantId).IsEqualTo("tenant-456");
  }

  /// <summary>
  /// When cascade has null envelope with both UserId and TenantId, both should propagate.
  /// Proves full security context flows through AsyncLocal correctly.
  /// </summary>
  [Test]
  public async Task Cascade_WithNullEnvelope_PropagatesBothUserAndTenantAsync() {
    // Arrange
    NullEnvelopeCascadeTracker.Reset();
    var (dispatcher, _) = _createDispatcher();
    var command = new NullEnvelopeCommand("test-data", Guid.NewGuid());

    // Act - Dispatch with both UserId and TenantId
    await dispatcher.RunAs("user-999").ForTenant("tenant-789").SendAsync(command);

    // Assert - Event receptor should see both
    await Assert.That(NullEnvelopeCascadeTracker.Count).IsEqualTo(1);
    var contexts = NullEnvelopeCascadeTracker.GetCapturedContexts();
    await Assert.That(contexts[0].userId).IsEqualTo("user-999");
    await Assert.That(contexts[0].tenantId).IsEqualTo("tenant-789");
  }

  /// <summary>
  /// When there's no parent security context, EstablishMessageContextForCascade() should not throw.
  /// Receptors should run with null context gracefully.
  /// </summary>
  [Test]
  public async Task Cascade_WithNullEnvelope_NoParentContext_DoesNotThrowAsync() {
    // Arrange
    NullEnvelopeCascadeTracker.Reset();
    var (dispatcher, _) = _createDispatcher();
    var command = new NullEnvelopeCommand("test-data", Guid.NewGuid());

    // Act - Dispatch WITHOUT any security context (no WithTenant, no RunAs)
    await dispatcher.SendAsync(command);

    // Assert - Should not throw, event receptor should be called
    await Assert.That(NullEnvelopeCascadeTracker.Count).IsEqualTo(1);

    // Context will be null - this is OK, the important part is no exception was thrown
    var contexts = NullEnvelopeCascadeTracker.GetCapturedContexts();
    await Assert.That(contexts[0].userId).IsNull();
    await Assert.That(contexts[0].tenantId).IsNull();
  }

  // ============================================
  // Helper Methods
  // ============================================

  private static (IDispatcher, IServiceProvider) _createDispatcher() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register message context accessor (required for cascade receptors)
    services.AddScoped<IMessageContextAccessor, Whizbang.Core.Security.MessageContextAccessor>();

    // Register all receptors including our test receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    return (dispatcher, serviceProvider);
  }
}
