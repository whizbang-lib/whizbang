using Whizbang.Core;
using Whizbang.Transports.FastEndpoints;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="RestMutationEndpointBase{TCommand, TResult}"/>.
/// Verifies FastEndpoints-specific mutation endpoint behavior.
/// </summary>
/// <tests>src/Whizbang.Transports.FastEndpoints/Endpoints/RestMutationEndpointBase.cs</tests>
public class RestMutationEndpointBaseTests {
  [Test]
  public async Task Endpoint_ShouldBeAbstractAsync() {
    // Assert
    var type = typeof(RestMutationEndpointBase<TestCommand, TestResult>);
    await Assert.That(type.IsAbstract).IsTrue();
  }

  [Test]
  public async Task Endpoint_ShouldInheritFromMutationEndpointBaseAsync() {
    // Assert
    var type = typeof(RestMutationEndpointBase<TestCommand, TestResult>);
    var baseType = typeof(MutationEndpointBase<TestCommand, TestResult>);
    await Assert.That(type.BaseType).IsEqualTo(baseType);
  }

  [Test]
  public async Task Execute_ShouldCallOnBeforeExecuteAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "test" };

    // Act
    await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.OnBeforeExecuteCalled).IsTrue();
  }

  [Test]
  public async Task Execute_ShouldCallOnAfterExecuteAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "test" };

    // Act
    await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.OnAfterExecuteCalled).IsTrue();
  }

  [Test]
  public async Task Execute_ShouldDispatchCommandAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "hello" };

    // Act
    var result = await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Value).IsEqualTo("hello_processed");
  }

  [Test]
  public async Task Execute_WhenDispatchThrows_ShouldCallOnErrorAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint { ShouldThrowOnDispatch = true };
    var command = new TestCommand { Value = "test" };

    // Act
    try {
      await endpoint.TestExecuteAsync(command, CancellationToken.None);
    } catch {
      // Expected
    }

    // Assert
    await Assert.That(endpoint.OnErrorCalled).IsTrue();
  }

  [Test]
  public async Task Execute_WhenOnErrorReturnsResult_ShouldReturnThatResultAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint {
      ShouldThrowOnDispatch = true,
      FallbackResult = new TestResult { Success = false, Value = "fallback" }
    };
    var command = new TestCommand { Value = "test" };

    // Act
    var result = await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Value).IsEqualTo("fallback");
  }

  [Test]
  public async Task Execute_WhenOnErrorReturnsNull_ShouldRethrowAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint {
      ShouldThrowOnDispatch = true,
      FallbackResult = null
    };
    var command = new TestCommand { Value = "test" };

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await endpoint.TestExecuteAsync(command, CancellationToken.None));
  }

  [Test]
  public async Task Execute_ShouldPassCommandToDispatchAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "specific-value" };

    // Act
    var result = await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.LastDispatchedCommand).IsEqualTo(command);
  }

  [Test]
  public async Task Execute_ShouldPassCancellationTokenAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "test" };
    using var cts = new CancellationTokenSource();

    // Act
    await endpoint.TestExecuteAsync(command, cts.Token);

    // Assert
    await Assert.That(endpoint.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Execute_WhenCancelled_ShouldThrowOperationCanceledAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "test" };
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await endpoint.TestExecuteAsync(command, cts.Token));
  }

  [Test]
  public async Task ExecuteWithRequest_ShouldMapRequestToCommandAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpointWithMapping();
    var request = new TestRequest { Input = "mapped" };

    // Act
    var result = await endpoint.TestExecuteWithRequestAsync(request, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.MapRequestToCommandCalled).IsTrue();
    await Assert.That(result.Value).IsEqualTo("mapped_processed");
  }

  [Test]
  public async Task ExecuteWithRequest_WithoutMapping_ShouldThrowNotImplementedAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var request = new TestRequest { Input = "test" };

    // Act & Assert
    await Assert.ThrowsAsync<NotImplementedException>(async () =>
        await endpoint.TestExecuteWithRequestAsync(request, CancellationToken.None));
  }

  [Test]
  public async Task OnBeforeExecute_ShouldReceiveCommandAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "before-test" };

    // Act
    await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.BeforeCommand?.Value).IsEqualTo("before-test");
  }

  [Test]
  public async Task OnAfterExecute_ShouldReceiveResultAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "after-test" };

    // Act
    await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.AfterResult?.Value).IsEqualTo("after-test_processed");
  }

  [Test]
  public async Task OnError_ShouldReceiveExceptionAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint { ShouldThrowOnDispatch = true };
    var command = new TestCommand { Value = "error-test" };

    // Act
    try {
      await endpoint.TestExecuteAsync(command, CancellationToken.None);
    } catch {
      // Expected
    }

    // Assert
    await Assert.That(endpoint.ErrorException).IsNotNull();
    await Assert.That(endpoint.ErrorException).IsTypeOf<InvalidOperationException>();
  }

  [Test]
  public async Task Context_ShouldContainCancellationTokenAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpoint();
    var command = new TestCommand { Value = "context-test" };
    using var cts = new CancellationTokenSource();

    // Act
    await endpoint.TestExecuteAsync(command, cts.Token);

    // Assert
    await Assert.That(endpoint.CapturedContext?.CancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Context_ShouldSupportItemsAsync() {
    // Arrange
    var endpoint = new TestRestMutationEndpointWithContextItems();
    var command = new TestCommand { Value = "items-test" };

    // Act
    await endpoint.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(endpoint.ItemsAccessedSuccessfully).IsTrue();
  }
}

/// <summary>
/// Test command for mutation endpoint tests.
/// </summary>
public class TestCommand : ICommand {
  public required string Value { get; init; }
}

/// <summary>
/// Test result for mutation endpoint tests.
/// </summary>
public class TestResult {
  public bool Success { get; init; }
  public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Test request DTO for custom mapping tests.
/// </summary>
public class TestRequest {
  public required string Input { get; init; }
}

/// <summary>
/// Test implementation of RestMutationEndpointBase for testing.
/// </summary>
public class TestRestMutationEndpoint : RestMutationEndpointBase<TestCommand, TestResult> {
  public bool OnBeforeExecuteCalled { get; private set; }
  public bool OnAfterExecuteCalled { get; private set; }
  public bool OnErrorCalled { get; private set; }
  public bool ShouldThrowOnDispatch { get; set; }
  public TestResult? FallbackResult { get; set; }
  public TestCommand? LastDispatchedCommand { get; private set; }
  public CancellationToken LastCancellationToken { get; private set; }
  public TestCommand? BeforeCommand { get; private set; }
  public TestResult? AfterResult { get; private set; }
  public Exception? ErrorException { get; private set; }
  public IMutationContext? CapturedContext { get; private set; }

  protected override ValueTask OnBeforeExecuteAsync(
      TestCommand command,
      IMutationContext context,
      CancellationToken ct) {
    OnBeforeExecuteCalled = true;
    BeforeCommand = command;
    CapturedContext = context;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestCommand command,
      TestResult result,
      IMutationContext context,
      CancellationToken ct) {
    OnAfterExecuteCalled = true;
    AfterResult = result;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestResult?> OnErrorAsync(
      TestCommand command,
      Exception ex,
      IMutationContext context,
      CancellationToken ct) {
    OnErrorCalled = true;
    ErrorException = ex;
    return ValueTask.FromResult(FallbackResult);
  }

  protected override ValueTask<TestResult> DispatchCommandAsync(
      TestCommand command,
      CancellationToken ct) {
    LastDispatchedCommand = command;
    LastCancellationToken = ct;

    if (ShouldThrowOnDispatch) {
      throw new InvalidOperationException("Test exception");
    }

    return ValueTask.FromResult(new TestResult {
      Success = true,
      Value = $"{command.Value}_processed"
    });
  }

  // Expose protected methods for testing
  public ValueTask<TestResult> TestExecuteAsync(TestCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);

  public ValueTask<TestResult> TestExecuteWithRequestAsync<TRequest>(TRequest request, CancellationToken ct)
      where TRequest : notnull
      => ExecuteWithRequestAsync(request, ct);
}

/// <summary>
/// Test implementation with custom request mapping.
/// </summary>
public class TestRestMutationEndpointWithMapping : RestMutationEndpointBase<TestCommand, TestResult> {
  public bool MapRequestToCommandCalled { get; private set; }

  protected override ValueTask<TestCommand> MapRequestToCommandAsync<TRequest>(
      TRequest request,
      CancellationToken ct) {
    MapRequestToCommandCalled = true;
    var testRequest = request as TestRequest;
    return ValueTask.FromResult(new TestCommand { Value = testRequest!.Input });
  }

  protected override ValueTask<TestResult> DispatchCommandAsync(
      TestCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestResult {
      Success = true,
      Value = $"{command.Value}_processed"
    });
  }

  public ValueTask<TestResult> TestExecuteWithRequestAsync<TRequest>(TRequest request, CancellationToken ct)
      where TRequest : notnull
      => ExecuteWithRequestAsync(request, ct);
}

/// <summary>
/// Test implementation that uses context items.
/// </summary>
public class TestRestMutationEndpointWithContextItems : RestMutationEndpointBase<TestCommand, TestResult> {
  public bool ItemsAccessedSuccessfully { get; private set; }

  protected override ValueTask OnBeforeExecuteAsync(
      TestCommand command,
      IMutationContext context,
      CancellationToken ct) {
    context.Items["test-key"] = "test-value";
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestCommand command,
      TestResult result,
      IMutationContext context,
      CancellationToken ct) {
    ItemsAccessedSuccessfully = context.Items.TryGetValue("test-key", out var value)
        && value as string == "test-value";
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestResult> DispatchCommandAsync(
      TestCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestResult { Success = true, Value = command.Value });
  }

  public ValueTask<TestResult> TestExecuteAsync(TestCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);
}
