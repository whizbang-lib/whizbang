using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for _getScopeDeltaForHop Priority 1 path - when IMessageContext directly
/// carries UserId/TenantId. Complements DispatcherSecurityPropagationTests which
/// only covers the ambient AsyncLocal path (Priority 2).
/// </summary>
[Category("Security")]
[Category("Dispatcher")]
public class DispatcherSecurityTests {

  // ========================================
  // Test Message Types
  // ========================================

  public record SecureCommand(string Data);
  public record SecureResult(string Data);

  public record SecureVoidCommand(string Data);

  public class SecureCommandReceptor : IReceptor<SecureCommand, SecureResult> {
    public ValueTask<SecureResult> HandleAsync(SecureCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new SecureResult(message.Data));
    }
  }

  public class SecureVoidCommandReceptor : IReceptor<SecureVoidCommand> {
    public int InvokedCount { get; private set; }

    public ValueTask HandleAsync(SecureVoidCommand message, CancellationToken cancellationToken = default) {
      InvokedCount++;
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // Priority 1 path: MessageContext with UserId/TenantId
  // The _getScopeDeltaForHop method checks context.UserId/TenantId first (Priority 1)
  // before falling back to ambient AsyncLocal scope (Priority 2).
  // ========================================

  [Test]
  public async Task SendAsync_WithContextContainingUserId_CompletesSuccessfullyAsync() {
    // Arrange - create a context with UserId set directly (Priority 1 path)
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("user-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = null
    };

    // Act - this exercises _getScopeDeltaForHop Priority 1 path (UserId is set)
    var receipt = await dispatcher.SendAsync((object)command, context);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithContextContainingTenantId_CompletesSuccessfullyAsync() {
    // Arrange - create a context with TenantId set directly (Priority 1 path)
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("tenant-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = null,
      TenantId = "tenant-456"
    };

    // Act - this exercises _getScopeDeltaForHop Priority 1 path (TenantId is set)
    var receipt = await dispatcher.SendAsync((object)command, context);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithContextContainingBothUserIdAndTenantId_CompletesSuccessfullyAsync() {
    // Arrange - both UserId and TenantId set (Priority 1 path - most specific case)
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("full-security-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-789",
      TenantId = "tenant-abc"
    };

    // Act
    var receipt = await dispatcher.SendAsync((object)command, context);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeAsync_WithContextContainingUserId_CompletesSuccessfullyAsync() {
    // Arrange - test Priority 1 path through LocalInvokeAsync
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("local-user-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-local-123",
      TenantId = null
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<SecureResult>((object)command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("local-user-data");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithContextContainingTenantId_CompletesSuccessfullyAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new SecureVoidCommand("void-tenant-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = null,
      TenantId = "tenant-void-789"
    };

    // Act - void invocation with Priority 1 path
    await dispatcher.LocalInvokeAsync((object)command, context);

    // No result to assert - just verify it doesn't throw
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_WithUserId_CompletesSuccessfullyAsync() {
    // Arrange - combined context + options overload with Priority 1 security
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("options-and-context-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-options-456",
      TenantId = "tenant-options-123"
    };
    var options = new DispatchOptions();

    // Act - this hits SendAsync(object, IMessageContext, DispatchOptions) with security context
    var receipt = await dispatcher.SendAsync((object)command, context, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithEmptyUserIdAndTenantId_FallsBackToAmbientScopeAsync() {
    // Arrange - context with empty strings (not null) - should use Priority 1 check fails,
    // falls to Priority 2 (ambient scope, which is null in test env = no scope delta)
    var dispatcher = _createDispatcher();
    var command = new SecureCommand("empty-security-data");
    var context = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = string.Empty,
      TenantId = string.Empty
    };

    // Act - empty strings fail Priority 1 check (IsNullOrEmpty), falls to Priority 2
    var receipt = await dispatcher.SendAsync((object)command, context);

    // Assert - should still complete successfully
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
