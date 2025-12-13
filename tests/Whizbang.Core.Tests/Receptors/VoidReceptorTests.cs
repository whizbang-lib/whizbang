using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Receptors;

/// <summary>
/// Tests for void receptor pattern (IReceptor&lt;TMessage&gt;).
/// Void receptors handle commands/events without returning results, enabling zero-allocation patterns.
/// </summary>
[Category("Receptors")]
public class VoidReceptorTests : DiagnosticTestBase {
  protected override DiagnosticCategory _diagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  // Test Messages for void pattern
  public record ProcessPaymentCommand(Guid OrderId, decimal Amount, string PaymentMethod);
  public record SendEmailCommand(string To, string Subject, string Body);
  public record LogEventCommand(string EventType, string Message);

  // Test void receptor implementations
  public class ProcessPaymentReceptor : IReceptor<ProcessPaymentCommand> {
    public int ProcessedCount { get; private set; }
    public ProcessPaymentCommand? LastProcessed { get; private set; }

    public ValueTask HandleAsync(ProcessPaymentCommand message, CancellationToken cancellationToken = default) {
      // Validation
      if (message.Amount <= 0) {
        throw new InvalidOperationException("Payment amount must be positive");
      }

      // Track for testing
      ProcessedCount++;
      LastProcessed = message;

      // No result needed - this is the zero allocation pattern
      return ValueTask.CompletedTask;
    }
  }

  public class SendEmailReceptor : IReceptor<SendEmailCommand> {
    public int EmailsSent { get; private set; }

    public async ValueTask HandleAsync(SendEmailCommand message, CancellationToken cancellationToken = default) {
      // Simulate async I/O
      await Task.Delay(1, cancellationToken);

      EmailsSent++;
    }
  }

  public class LogEventReceptor : IReceptor<LogEventCommand> {
    public List<string> LoggedEvents { get; } = [];

    public ValueTask HandleAsync(LogEventCommand message, CancellationToken cancellationToken = default) {
      LoggedEvents.Add($"{message.EventType}: {message.Message}");
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task VoidReceptor_SynchronousCompletion_ShouldCompleteWithoutAllocationAsync() {
    // Arrange
    var receptor = new ProcessPaymentReceptor();
    var command = new ProcessPaymentCommand(
      OrderId: Guid.NewGuid(),
      Amount: 100.00m,
      PaymentMethod: "CreditCard"
    );

    // Act
    var task = receptor.HandleAsync(command);

    // Assert - Synchronous completion
    await Assert.That(task.IsCompleted).IsTrue();
    await Assert.That(receptor.ProcessedCount).IsEqualTo(1);
    await Assert.That(receptor.LastProcessed).IsEqualTo(command);
  }

  [Test]
  public async Task VoidReceptor_AsynchronousCompletion_ShouldCompleteAsync() {
    // Arrange
    var receptor = new SendEmailReceptor();
    var command = new SendEmailCommand(
      To: "user@example.com",
      Subject: "Test",
      Body: "Test message"
    );

    // Act
    var task = receptor.HandleAsync(command);
    await Assert.That(task.IsCompleted).IsFalse(); // Async operation
    await task;

    // Assert
    await Assert.That(receptor.EmailsSent).IsEqualTo(1);
  }

  [Test]
  public async Task VoidReceptor_Validation_ShouldThrowExceptionAsync() {
    // Arrange
    var receptor = new ProcessPaymentReceptor();
    var command = new ProcessPaymentCommand(
      OrderId: Guid.NewGuid(),
      Amount: -10.00m, // Invalid amount
      PaymentMethod: "CreditCard"
    );

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessage("Payment amount must be positive");
  }

  [Test]
  public async Task VoidReceptor_MultipleInvocations_ShouldBeStatelessAsync() {
    // Arrange
    var receptor = new LogEventReceptor();
    var command1 = new LogEventCommand("OrderPlaced", "Order 123 placed");
    var command2 = new LogEventCommand("PaymentProcessed", "Payment for order 123");

    // Act
    await receptor.HandleAsync(command1);
    await receptor.HandleAsync(command2);

    // Assert
    await Assert.That(receptor.LoggedEvents).HasCount().EqualTo(2);
    await Assert.That(receptor.LoggedEvents[0]).Contains("OrderPlaced");
    await Assert.That(receptor.LoggedEvents[1]).Contains("PaymentProcessed");
  }

  [Test]
  public async Task VoidReceptor_CancellationToken_ShouldRespectCancellationAsync() {
    // Arrange
    var receptor = new SendEmailReceptor();
    var command = new SendEmailCommand("user@example.com", "Test", "Message");
    using var cts = new CancellationTokenSource();
    cts.Cancel(); // Cancel immediately

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command, cts.Token))
      .ThrowsExactly<TaskCanceledException>();
  }
}
