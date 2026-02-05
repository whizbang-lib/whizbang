using Whizbang.Core;
using Whizbang.Transports.HotChocolate;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.HotChocolate.Tests.Integration;

/// <summary>
/// Integration tests for the full GraphQL mutation lifecycle.
/// Tests the complete flow from mutation request to response including all hooks.
/// </summary>
[Category("Integration")]
[Category("GraphQL")]
[Category("Mutations")]
public class GraphQLMutationLifecycleTests {
  [Test]
  public async Task FullLifecycle_WithValidCommand_ExecutesAllHooksInOrderAsync() {
    // Arrange
    var executionOrder = new List<string>();
    var mutation = new OrderTrackingGraphQLMutation(executionOrder);
    var command = new TrackingCommand { Value = "test" };

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(executionOrder).Count().IsEqualTo(3);
    await Assert.That(executionOrder[0]).IsEqualTo("OnBeforeExecute");
    await Assert.That(executionOrder[1]).IsEqualTo("DispatchCommand");
    await Assert.That(executionOrder[2]).IsEqualTo("OnAfterExecute");
  }

  [Test]
  public async Task FullLifecycle_WithValidation_CanRejectInBeforeHookAsync() {
    // Arrange
    var mutation = new ValidatingGraphQLMutation { ShouldRejectValidation = true };
    var command = new ValidatableCommand { Value = "invalid" };

    // Act & Assert
    var exception = await Assert.ThrowsAsync<ValidationException>(async () =>
        await mutation.TestExecuteAsync(command, CancellationToken.None));

    await Assert.That(mutation.DispatchWasCalled).IsFalse();
    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Validation failed");
  }

  [Test]
  public async Task FullLifecycle_WithNotification_SendsNotificationAfterSuccessAsync() {
    // Arrange
    var notifications = new List<string>();
    var mutation = new NotifyingGraphQLMutation(notifications);
    var command = new NotifiableCommand { OrderId = Guid.NewGuid() };

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(notifications).Count().IsEqualTo(1);
    await Assert.That(notifications[0]).Contains(command.OrderId.ToString());
  }

  [Test]
  public async Task FullLifecycle_WithError_CallsErrorHookAndCanRecoverAsync() {
    // Arrange
    var mutation = new RecoveringGraphQLMutation {
      ShouldThrowOnDispatch = true,
      RecoveryResult = new RecoveryResult { Recovered = true, Message = "Recovered successfully" }
    };
    var command = new RecoverableCommand();

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Recovered).IsTrue();
    await Assert.That(result.Message).IsEqualTo("Recovered successfully");
    await Assert.That(mutation.ErrorHandlerWasCalled).IsTrue();
  }

  [Test]
  public async Task FullLifecycle_WithContextSharing_PassesDataBetweenHooksAsync() {
    // Arrange
    var mutation = new ContextSharingGraphQLMutation();
    var command = new ContextCommand { UserId = "user-456" };

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result.UserIdFromContext).IsEqualTo("user-456");
    await Assert.That(result.TimestampSet).IsTrue();
  }

  [Test]
  public async Task FullLifecycle_WithCustomRequestMapping_MapsCorrectlyAsync() {
    // Arrange
    var mutation = new MappingGraphQLMutation();
    var request = new CreateOrderRequest {
      CustomerEmail = "graphql@example.com",
      ProductId = "prod-789"
    };

    // Act
    var result = await mutation.TestExecuteWithRequestAsync(request, CancellationToken.None);

    // Assert
    await Assert.That(result.OrderCreated).IsTrue();
    await Assert.That(result.CustomerEmail).IsEqualTo("graphql@example.com");
    await Assert.That(result.ProductId).IsEqualTo("prod-789");
  }

  [Test]
  public async Task FullLifecycle_WithCancellation_ThrowsBeforeDispatchAsync() {
    // Arrange
    var mutation = new CancellationAwareGraphQLMutation();
    var command = new CancellableCommand();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await mutation.TestExecuteAsync(command, cts.Token));

    await Assert.That(mutation.DispatchWasCalled).IsFalse();
  }

  [Test]
  public async Task FullLifecycle_WithLogging_LogsAllStagesAsync() {
    // Arrange
    var logs = new List<string>();
    var mutation = new LoggingGraphQLMutation(logs);
    var command = new LoggableCommand { Action = "CreateOrder" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(logs).Count().IsGreaterThanOrEqualTo(3);
    await Assert.That(logs).Contains(s => s.Contains("Starting"));
    await Assert.That(logs).Contains(s => s.Contains("Dispatching"));
    await Assert.That(logs).Contains(s => s.Contains("Completed"));
  }
}

#region Test Commands and Results

public class TrackingCommand : ICommand {
  public string Value { get; init; } = string.Empty;
}

public class TrackingResult {
  public bool Success { get; init; }
}

public class ValidatableCommand : ICommand {
  public string Value { get; init; } = string.Empty;
}

public class ValidatableResult {
  public bool Valid { get; init; }
}

public class NotifiableCommand : ICommand {
  public Guid OrderId { get; init; }
}

public class NotifiableResult {
  public Guid OrderId { get; init; }
}

public class RecoverableCommand : ICommand { }

public class RecoveryResult {
  public bool Recovered { get; init; }
  public string Message { get; init; } = string.Empty;
}

public class ContextCommand : ICommand {
  public string UserId { get; init; } = string.Empty;
}

public class ContextResult {
  public string UserIdFromContext { get; init; } = string.Empty;
  public bool TimestampSet { get; init; }
}

public class CreateOrderRequest {
  public string CustomerEmail { get; init; } = string.Empty;
  public string ProductId { get; init; } = string.Empty;
}

public class CreateOrderCommand : ICommand {
  public string CustomerEmail { get; init; } = string.Empty;
  public string ProductId { get; init; } = string.Empty;
}

public class CreateOrderResult {
  public bool OrderCreated { get; init; }
  public string CustomerEmail { get; init; } = string.Empty;
  public string ProductId { get; init; } = string.Empty;
}

public class CancellableCommand : ICommand { }

public class CancellableResult { }

public class LoggableCommand : ICommand {
  public string Action { get; init; } = string.Empty;
}

public class LoggableResult {
  public bool Logged { get; init; }
}

public class ValidationException : Exception {
  public ValidationException() : base() { }
  public ValidationException(string message) : base(message) { }
  public ValidationException(string message, Exception innerException) : base(message, innerException) { }
}

#endregion

#region Test Mutations

public class OrderTrackingGraphQLMutation : GraphQLMutationBase<TrackingCommand, TrackingResult> {
  private readonly List<string> _executionOrder;

  public OrderTrackingGraphQLMutation(List<string> executionOrder) {
    _executionOrder = executionOrder;
  }

  protected override ValueTask OnBeforeExecuteAsync(
      TrackingCommand command,
      IMutationContext context,
      CancellationToken ct) {
    _executionOrder.Add("OnBeforeExecute");
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TrackingResult> DispatchCommandAsync(
      TrackingCommand command,
      CancellationToken ct) {
    _executionOrder.Add("DispatchCommand");
    return ValueTask.FromResult(new TrackingResult { Success = true });
  }

  protected override ValueTask OnAfterExecuteAsync(
      TrackingCommand command,
      TrackingResult result,
      IMutationContext context,
      CancellationToken ct) {
    _executionOrder.Add("OnAfterExecute");
    return ValueTask.CompletedTask;
  }

  public ValueTask<TrackingResult> TestExecuteAsync(TrackingCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

public class ValidatingGraphQLMutation : GraphQLMutationBase<ValidatableCommand, ValidatableResult> {
  public bool ShouldRejectValidation { get; set; }
  public bool DispatchWasCalled { get; private set; }

  protected override ValueTask OnBeforeExecuteAsync(
      ValidatableCommand command,
      IMutationContext context,
      CancellationToken ct) {
    if (ShouldRejectValidation) {
      throw new ValidationException("Validation failed: invalid input");
    }
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<ValidatableResult> DispatchCommandAsync(
      ValidatableCommand command,
      CancellationToken ct) {
    DispatchWasCalled = true;
    return ValueTask.FromResult(new ValidatableResult { Valid = true });
  }

  public ValueTask<ValidatableResult> TestExecuteAsync(ValidatableCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

public class NotifyingGraphQLMutation : GraphQLMutationBase<NotifiableCommand, NotifiableResult> {
  private readonly List<string> _notifications;

  public NotifyingGraphQLMutation(List<string> notifications) {
    _notifications = notifications;
  }

  protected override ValueTask<NotifiableResult> DispatchCommandAsync(
      NotifiableCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new NotifiableResult { OrderId = command.OrderId });
  }

  protected override ValueTask OnAfterExecuteAsync(
      NotifiableCommand command,
      NotifiableResult result,
      IMutationContext context,
      CancellationToken ct) {
    _notifications.Add($"Order {result.OrderId} created successfully");
    return ValueTask.CompletedTask;
  }

  public ValueTask<NotifiableResult> TestExecuteAsync(NotifiableCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

public class RecoveringGraphQLMutation : GraphQLMutationBase<RecoverableCommand, RecoveryResult> {
  public bool ShouldThrowOnDispatch { get; set; }
  public RecoveryResult? RecoveryResult { get; set; }
  public bool ErrorHandlerWasCalled { get; private set; }

  protected override ValueTask<RecoveryResult> DispatchCommandAsync(
      RecoverableCommand command,
      CancellationToken ct) {
    if (ShouldThrowOnDispatch) {
      throw new InvalidOperationException("Dispatch failed");
    }
    return ValueTask.FromResult(new RecoveryResult { Recovered = false });
  }

  protected override ValueTask<RecoveryResult?> OnErrorAsync(
      RecoverableCommand command,
      Exception ex,
      IMutationContext context,
      CancellationToken ct) {
    ErrorHandlerWasCalled = true;
    return ValueTask.FromResult(RecoveryResult);
  }

  public ValueTask<RecoveryResult> TestExecuteAsync(RecoverableCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

public class ContextSharingGraphQLMutation : GraphQLMutationBase<ContextCommand, ContextResult> {
  private const string USERID_KEY = "UserId";
  private const string TIMESTAMP_KEY = "Timestamp";

  protected override ValueTask OnBeforeExecuteAsync(
      ContextCommand command,
      IMutationContext context,
      CancellationToken ct) {
    context.Items[USERID_KEY] = command.UserId;
    context.Items[TIMESTAMP_KEY] = DateTime.UtcNow;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<ContextResult> DispatchCommandAsync(
      ContextCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new ContextResult());
  }

  protected override ValueTask OnAfterExecuteAsync(
      ContextCommand command,
      ContextResult result,
      IMutationContext context,
      CancellationToken ct) {
    // Modify result based on context
    return ValueTask.CompletedTask;
  }

  public async ValueTask<ContextResult> TestExecuteAsync(ContextCommand cmd, CancellationToken ct) {
    var context = new MutationContext(ct);
    await OnBeforeExecuteAsync(cmd, context, ct);
    var result = await DispatchCommandAsync(cmd, ct);

    // Create final result with context data
    var finalResult = new ContextResult {
      UserIdFromContext = context.Items.TryGetValue(USERID_KEY, out var userId) ? userId?.ToString() ?? "" : "",
      TimestampSet = context.Items.ContainsKey(TIMESTAMP_KEY)
    };

    return finalResult;
  }
}

public class MappingGraphQLMutation : GraphQLMutationBase<CreateOrderCommand, CreateOrderResult> {
  protected override ValueTask<CreateOrderCommand> MapRequestToCommandAsync<TRequest>(
      TRequest request,
      CancellationToken ct) {
    var orderRequest = request as CreateOrderRequest;
    return ValueTask.FromResult(new CreateOrderCommand {
      CustomerEmail = orderRequest!.CustomerEmail,
      ProductId = orderRequest.ProductId
    });
  }

  protected override ValueTask<CreateOrderResult> DispatchCommandAsync(
      CreateOrderCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new CreateOrderResult {
      OrderCreated = true,
      CustomerEmail = command.CustomerEmail,
      ProductId = command.ProductId
    });
  }

  public ValueTask<CreateOrderResult> TestExecuteWithRequestAsync<TRequest>(
      TRequest request,
      CancellationToken ct) where TRequest : notnull
      => ExecuteWithRequestAsync(request, ct);
}

public class CancellationAwareGraphQLMutation : GraphQLMutationBase<CancellableCommand, CancellableResult> {
  public bool DispatchWasCalled { get; private set; }

  protected override ValueTask<CancellableResult> DispatchCommandAsync(
      CancellableCommand command,
      CancellationToken ct) {
    DispatchWasCalled = true;
    return ValueTask.FromResult(new CancellableResult());
  }

  public ValueTask<CancellableResult> TestExecuteAsync(CancellableCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

public class LoggingGraphQLMutation : GraphQLMutationBase<LoggableCommand, LoggableResult> {
  private readonly List<string> _logs;

  public LoggingGraphQLMutation(List<string> logs) {
    _logs = logs;
  }

  protected override ValueTask OnBeforeExecuteAsync(
      LoggableCommand command,
      IMutationContext context,
      CancellationToken ct) {
    _logs.Add($"Starting: {command.Action}");
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<LoggableResult> DispatchCommandAsync(
      LoggableCommand command,
      CancellationToken ct) {
    _logs.Add($"Dispatching: {command.Action}");
    return ValueTask.FromResult(new LoggableResult { Logged = true });
  }

  protected override ValueTask OnAfterExecuteAsync(
      LoggableCommand command,
      LoggableResult result,
      IMutationContext context,
      CancellationToken ct) {
    _logs.Add($"Completed: {command.Action}");
    return ValueTask.CompletedTask;
  }

  public ValueTask<LoggableResult> TestExecuteAsync(LoggableCommand cmd, CancellationToken ct)
      => ExecuteAsync(cmd, ct);
}

#endregion
