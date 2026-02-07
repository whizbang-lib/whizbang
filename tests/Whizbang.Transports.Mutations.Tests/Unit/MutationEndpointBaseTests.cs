using Whizbang.Core;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.Mutations.Tests.Unit;

/// <summary>
/// Tests for <see cref="MutationEndpointBase{TCommand, TResult}"/>.
/// Verifies hook execution, command dispatch, and error handling.
/// </summary>
public class MutationEndpointBaseTests {
  [Test]
  public async Task ExecuteAsync_ShouldCallOnBeforeExecuteAsync() {
    // Arrange
    var endpoint = new TestMutationEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act
    await endpoint.TestExecuteAsync(command, ct);

    // Assert
    await Assert.That(endpoint.BeforeExecuteCalled).IsTrue();
    await Assert.That(endpoint.BeforeCommand).IsEqualTo(command);
  }

  [Test]
  public async Task ExecuteAsync_ShouldCallOnAfterExecuteAsync() {
    // Arrange
    var endpoint = new TestMutationEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act
    var result = await endpoint.TestExecuteAsync(command, ct);

    // Assert
    await Assert.That(endpoint.AfterExecuteCalled).IsTrue();
    await Assert.That(endpoint.AfterResult).IsEqualTo(result);
    await Assert.That(endpoint.AfterCommand).IsEqualTo(command);
  }

  [Test]
  public async Task ExecuteAsync_ShouldCallOnErrorAsync_WhenDispatchThrowsAsync() {
    // Arrange
    var endpoint = new TestMutationEndpoint { ShouldThrowOnDispatch = true };
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act & Assert - should call error hook and rethrow since OnErrorAsync returns null
    await Assert.ThrowsAsync<InvalidOperationException>(
        async () => await endpoint.TestExecuteAsync(command, ct));

    await Assert.That(endpoint.ErrorCalled).IsTrue();
    await Assert.That(endpoint.ErrorException).IsNotNull();
    await Assert.That(endpoint.ErrorCommand).IsEqualTo(command);
  }

  [Test]
  public async Task ExecuteAsync_ShouldReturnErrorResult_WhenOnErrorAsyncReturnsValueAsync() {
    // Arrange
    var errorResult = new TestOrderResult { OrderId = "error-order" };
    var endpoint = new TestMutationEndpoint {
      ShouldThrowOnDispatch = true,
      ErrorResultToReturn = errorResult
    };
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act
    var result = await endpoint.TestExecuteAsync(command, ct);

    // Assert - should return error result instead of throwing
    await Assert.That(result).IsEqualTo(errorResult);
    await Assert.That(endpoint.ErrorCalled).IsTrue();
    await Assert.That(endpoint.AfterExecuteCalled).IsFalse(); // OnAfterExecute should not be called on error
  }

  [Test]
  public async Task ExecuteAsync_ShouldPassContextToHooksAsync() {
    // Arrange
    var endpoint = new TestMutationEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act
    await endpoint.TestExecuteAsync(command, ct);

    // Assert - context should be passed to hooks
    await Assert.That(endpoint.BeforeContext).IsNotNull();
    await Assert.That(endpoint.AfterContext).IsNotNull();
    await Assert.That(endpoint.BeforeContext!.CancellationToken).IsEqualTo(ct);
  }

  [Test]
  public async Task OnBeforeExecuteAsync_Default_ShouldCompleteImmediatelyAsync() {
    // Arrange
    var endpoint = new DefaultHookEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act & Assert - should not throw
    await endpoint.TestOnBeforeExecuteAsync(command, ct);
  }

  [Test]
  public async Task OnAfterExecuteAsync_Default_ShouldCompleteImmediatelyAsync() {
    // Arrange
    var endpoint = new DefaultHookEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var result = new TestOrderResult { OrderId = "order-456" };
    var ct = CancellationToken.None;

    // Act & Assert - should not throw
    await endpoint.TestOnAfterExecuteAsync(command, result, ct);
  }

  [Test]
  public async Task OnErrorAsync_Default_ShouldReturnNullAsync() {
    // Arrange
    var endpoint = new DefaultHookEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var exception = new InvalidOperationException("Test error");
    var ct = CancellationToken.None;

    // Act
    var result = await endpoint.TestOnErrorAsync(command, exception, ct);

    // Assert - default returns null (rethrow)
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExecuteAsync_ShouldRespectCancellationTokenAsync() {
    // Arrange
    var endpoint = new TestMutationEndpoint();
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(
        async () => await endpoint.TestExecuteAsync(command, cts.Token));
  }

  [Test]
  public async Task ExecuteAsync_Order_ShouldBe_Before_Dispatch_AfterAsync() {
    // Arrange
    var executionOrder = new List<string>();
    var endpoint = new OrderTrackingEndpoint(executionOrder);
    var command = new TestOrderCommand { CustomerId = "cust-123" };
    var ct = CancellationToken.None;

    // Act
    await endpoint.TestExecuteAsync(command, ct);

    // Assert - order should be Before -> Dispatch -> After
    await Assert.That(executionOrder).Count().IsEqualTo(3);
    await Assert.That(executionOrder[0]).IsEqualTo("Before");
    await Assert.That(executionOrder[1]).IsEqualTo("Dispatch");
    await Assert.That(executionOrder[2]).IsEqualTo("After");
  }

  [Test]
  public async Task MapRequestToCommandAsync_Default_ShouldThrowNotImplementedAsync() {
    // Arrange
    var endpoint = new DefaultHookEndpoint();
    var request = new TestOrderRequest { Data = "test" };
    var ct = CancellationToken.None;

    // Act & Assert - default should throw NotImplementedException
    await Assert.ThrowsAsync<NotImplementedException>(
        async () => await endpoint.TestMapRequestToCommandAsync(request, ct));
  }

  [Test]
  public async Task MapRequestToCommandAsync_WhenOverridden_ShouldBeCalledAsync() {
    // Arrange
    var endpoint = new MappingEndpoint();
    var request = new TestOrderRequest { Data = "cust-999" };
    var ct = CancellationToken.None;

    // Act
    var command = await endpoint.TestMapRequestToCommandAsync(request, ct);

    // Assert
    await Assert.That(command.CustomerId).IsEqualTo("cust-999");
  }

  [Test]
  public async Task ExecuteWithRequestAsync_ShouldCallMapRequestToCommandAsync() {
    // Arrange
    var endpoint = new MappingEndpoint();
    var request = new TestOrderRequest { Data = "cust-mapped" };
    var ct = CancellationToken.None;

    // Act
    var result = await endpoint.TestExecuteWithRequestAsync(request, ct);

    // Assert
    await Assert.That(endpoint.MapRequestCalled).IsTrue();
    await Assert.That(result.OrderId).Contains("mapped");
  }
}

// Test implementations

public class TestOrderCommand : ICommand {
  public required string CustomerId { get; init; }
}

public class TestOrderResult {
  public string OrderId { get; set; } = string.Empty;
}

public class TestOrderRequest {
  public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Test endpoint that tracks hook calls.
/// </summary>
public class TestMutationEndpoint : MutationEndpointBase<TestOrderCommand, TestOrderResult> {
  public bool BeforeExecuteCalled { get; private set; }
  public bool AfterExecuteCalled { get; private set; }
  public bool ErrorCalled { get; private set; }

  public TestOrderCommand? BeforeCommand { get; private set; }
  public TestOrderCommand? AfterCommand { get; private set; }
  public TestOrderCommand? ErrorCommand { get; private set; }

  public TestOrderResult? AfterResult { get; private set; }
  public Exception? ErrorException { get; private set; }

  public IMutationContext? BeforeContext { get; private set; }
  public IMutationContext? AfterContext { get; private set; }

  public bool ShouldThrowOnDispatch { get; set; }
  public TestOrderResult? ErrorResultToReturn { get; set; }

  protected override ValueTask OnBeforeExecuteAsync(
      TestOrderCommand command,
      IMutationContext context,
      CancellationToken ct) {
    BeforeExecuteCalled = true;
    BeforeCommand = command;
    BeforeContext = context;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestOrderCommand command,
      TestOrderResult result,
      IMutationContext context,
      CancellationToken ct) {
    AfterExecuteCalled = true;
    AfterCommand = command;
    AfterResult = result;
    AfterContext = context;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestOrderResult?> OnErrorAsync(
      TestOrderCommand command,
      Exception ex,
      IMutationContext context,
      CancellationToken ct) {
    ErrorCalled = true;
    ErrorCommand = command;
    ErrorException = ex;
    return ValueTask.FromResult(ErrorResultToReturn);
  }

  protected override ValueTask<TestOrderResult> DispatchCommandAsync(
      TestOrderCommand command,
      CancellationToken ct) {
    if (ShouldThrowOnDispatch) {
      throw new InvalidOperationException("Dispatch error");
    }
    return ValueTask.FromResult(new TestOrderResult { OrderId = $"order-{command.CustomerId}" });
  }

  // Expose for testing
  public ValueTask<TestOrderResult> TestExecuteAsync(TestOrderCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);
}

/// <summary>
/// Endpoint with default hook implementations for testing defaults.
/// </summary>
public class DefaultHookEndpoint : MutationEndpointBase<TestOrderCommand, TestOrderResult> {
  protected override ValueTask<TestOrderResult> DispatchCommandAsync(
      TestOrderCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestOrderResult { OrderId = "default-order" });
  }

  // Expose protected methods for testing
  public ValueTask TestOnBeforeExecuteAsync(TestOrderCommand command, CancellationToken ct)
      => OnBeforeExecuteAsync(command, new MutationContext(ct), ct);

  public ValueTask TestOnAfterExecuteAsync(TestOrderCommand command, TestOrderResult result, CancellationToken ct)
      => OnAfterExecuteAsync(command, result, new MutationContext(ct), ct);

  public ValueTask<TestOrderResult?> TestOnErrorAsync(TestOrderCommand command, Exception ex, CancellationToken ct)
      => OnErrorAsync(command, ex, new MutationContext(ct), ct);

  public ValueTask<TestOrderCommand> TestMapRequestToCommandAsync<TRequest>(TRequest request, CancellationToken ct)
      where TRequest : notnull
      => MapRequestToCommandAsync(request, ct);
}

/// <summary>
/// Endpoint that tracks execution order.
/// </summary>
public class OrderTrackingEndpoint : MutationEndpointBase<TestOrderCommand, TestOrderResult> {
  private readonly List<string> _executionOrder;

  public OrderTrackingEndpoint(List<string> executionOrder) {
    _executionOrder = executionOrder;
  }

  protected override ValueTask OnBeforeExecuteAsync(
      TestOrderCommand command,
      IMutationContext context,
      CancellationToken ct) {
    _executionOrder.Add("Before");
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestOrderCommand command,
      TestOrderResult result,
      IMutationContext context,
      CancellationToken ct) {
    _executionOrder.Add("After");
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestOrderResult> DispatchCommandAsync(
      TestOrderCommand command,
      CancellationToken ct) {
    _executionOrder.Add("Dispatch");
    return ValueTask.FromResult(new TestOrderResult { OrderId = "tracked-order" });
  }

  public ValueTask<TestOrderResult> TestExecuteAsync(TestOrderCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);
}

/// <summary>
/// Endpoint with request-to-command mapping.
/// </summary>
public class MappingEndpoint : MutationEndpointBase<TestOrderCommand, TestOrderResult> {
  public bool MapRequestCalled { get; private set; }

  protected override ValueTask<TestOrderCommand> MapRequestToCommandAsync<TRequest>(
      TRequest request,
      CancellationToken ct) {
    MapRequestCalled = true;
    if (request is TestOrderRequest orderRequest) {
      return ValueTask.FromResult(new TestOrderCommand { CustomerId = orderRequest.Data });
    }
    throw new InvalidOperationException("Unknown request type");
  }

  protected override ValueTask<TestOrderResult> DispatchCommandAsync(
      TestOrderCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestOrderResult { OrderId = $"order-{command.CustomerId}" });
  }

  public ValueTask<TestOrderCommand> TestMapRequestToCommandAsync(TestOrderRequest request, CancellationToken ct)
      => MapRequestToCommandAsync(request, ct);

  public ValueTask<TestOrderResult> TestExecuteWithRequestAsync(TestOrderRequest request, CancellationToken ct)
      => ExecuteWithRequestAsync(request, ct);
}
