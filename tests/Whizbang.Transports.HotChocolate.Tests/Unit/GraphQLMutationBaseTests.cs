using Whizbang.Core;
using Whizbang.Transports.HotChocolate;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="GraphQLMutationBase{TCommand, TResult}"/>.
/// Verifies HotChocolate-specific mutation endpoint behavior.
/// </summary>
/// <tests>src/Whizbang.Transports.HotChocolate/Mutations/GraphQLMutationBase.cs</tests>
public class GraphQLMutationBaseTests {
  [Test]
  public async Task Mutation_ShouldBeAbstractAsync() {
    // Assert
    var type = typeof(GraphQLMutationBase<TestMutationCommand, TestMutationResult>);
    await Assert.That(type.IsAbstract).IsTrue();
  }

  [Test]
  public async Task Mutation_ShouldInheritFromMutationEndpointBaseAsync() {
    // Assert
    var type = typeof(GraphQLMutationBase<TestMutationCommand, TestMutationResult>);
    var baseType = typeof(MutationEndpointBase<TestMutationCommand, TestMutationResult>);
    await Assert.That(type.BaseType).IsEqualTo(baseType);
  }

  [Test]
  public async Task Execute_ShouldCallOnBeforeExecuteAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "test" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.OnBeforeExecuteCalled).IsTrue();
  }

  [Test]
  public async Task Execute_ShouldCallOnAfterExecuteAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "test" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.OnAfterExecuteCalled).IsTrue();
  }

  [Test]
  public async Task Execute_ShouldDispatchCommandAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "hello" };

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Value).IsEqualTo("hello_processed");
  }

  [Test]
  public async Task Execute_WhenDispatchThrows_ShouldCallOnErrorAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation { ShouldThrowOnDispatch = true };
    var command = new TestMutationCommand { Value = "test" };

    // Act
    try {
      await mutation.TestExecuteAsync(command, CancellationToken.None);
    } catch {
      // Expected
    }

    // Assert
    await Assert.That(mutation.OnErrorCalled).IsTrue();
  }

  [Test]
  public async Task Execute_WhenOnErrorReturnsResult_ShouldReturnThatResultAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation {
      ShouldThrowOnDispatch = true,
      FallbackResult = new TestMutationResult { Success = false, Value = "fallback" }
    };
    var command = new TestMutationCommand { Value = "test" };

    // Act
    var result = await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Value).IsEqualTo("fallback");
  }

  [Test]
  public async Task Execute_WhenOnErrorReturnsNull_ShouldRethrowAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation {
      ShouldThrowOnDispatch = true,
      FallbackResult = null
    };
    var command = new TestMutationCommand { Value = "test" };

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await mutation.TestExecuteAsync(command, CancellationToken.None));
  }

  [Test]
  public async Task Execute_ShouldPassCommandToDispatchAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "specific-value" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.LastDispatchedCommand).IsEqualTo(command);
  }

  [Test]
  public async Task Execute_ShouldPassCancellationTokenAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "test" };
    using var cts = new CancellationTokenSource();

    // Act
    await mutation.TestExecuteAsync(command, cts.Token);

    // Assert
    await Assert.That(mutation.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Execute_WhenCancelled_ShouldThrowOperationCanceledAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "test" };
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await mutation.TestExecuteAsync(command, cts.Token));
  }

  [Test]
  public async Task ExecuteWithRequest_ShouldMapRequestToCommandAsync() {
    // Arrange
    var mutation = new TestGraphQLMutationWithMapping();
    var request = new TestMutationRequest { Input = "mapped" };

    // Act
    var result = await mutation.TestExecuteWithRequestAsync(request, CancellationToken.None);

    // Assert
    await Assert.That(mutation.MapRequestToCommandCalled).IsTrue();
    await Assert.That(result.Value).IsEqualTo("mapped_processed");
  }

  [Test]
  public async Task ExecuteWithRequest_WithoutMapping_ShouldThrowNotImplementedAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var request = new TestMutationRequest { Input = "test" };

    // Act & Assert
    await Assert.ThrowsAsync<NotImplementedException>(async () =>
        await mutation.TestExecuteWithRequestAsync(request, CancellationToken.None));
  }

  [Test]
  public async Task OnBeforeExecute_ShouldReceiveCommandAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "before-test" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.BeforeCommand?.Value).IsEqualTo("before-test");
  }

  [Test]
  public async Task OnAfterExecute_ShouldReceiveResultAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "after-test" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.AfterResult?.Value).IsEqualTo("after-test_processed");
  }

  [Test]
  public async Task OnError_ShouldReceiveExceptionAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation { ShouldThrowOnDispatch = true };
    var command = new TestMutationCommand { Value = "error-test" };

    // Act
    try {
      await mutation.TestExecuteAsync(command, CancellationToken.None);
    } catch {
      // Expected
    }

    // Assert
    await Assert.That(mutation.ErrorException).IsNotNull();
    await Assert.That(mutation.ErrorException).IsTypeOf<InvalidOperationException>();
  }

  [Test]
  public async Task Context_ShouldContainCancellationTokenAsync() {
    // Arrange
    var mutation = new TestGraphQLMutation();
    var command = new TestMutationCommand { Value = "context-test" };
    using var cts = new CancellationTokenSource();

    // Act
    await mutation.TestExecuteAsync(command, cts.Token);

    // Assert
    await Assert.That(mutation.CapturedContext?.CancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Context_ShouldSupportItemsAsync() {
    // Arrange
    var mutation = new TestGraphQLMutationWithContextItems();
    var command = new TestMutationCommand { Value = "items-test" };

    // Act
    await mutation.TestExecuteAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(mutation.ItemsAccessedSuccessfully).IsTrue();
  }
}

#region Test Types

/// <summary>
/// Test command for GraphQL mutation tests.
/// </summary>
public class TestMutationCommand : ICommand {
  public required string Value { get; init; }
}

/// <summary>
/// Test result for GraphQL mutation tests.
/// </summary>
public class TestMutationResult {
  public bool Success { get; init; }
  public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Test request DTO for custom mapping tests.
/// </summary>
public class TestMutationRequest {
  public required string Input { get; init; }
}

/// <summary>
/// Test implementation of GraphQLMutationBase for testing.
/// </summary>
public class TestGraphQLMutation : GraphQLMutationBase<TestMutationCommand, TestMutationResult> {
  public bool OnBeforeExecuteCalled { get; private set; }
  public bool OnAfterExecuteCalled { get; private set; }
  public bool OnErrorCalled { get; private set; }
  public bool ShouldThrowOnDispatch { get; set; }
  public TestMutationResult? FallbackResult { get; set; }
  public TestMutationCommand? LastDispatchedCommand { get; private set; }
  public CancellationToken LastCancellationToken { get; private set; }
  public TestMutationCommand? BeforeCommand { get; private set; }
  public TestMutationResult? AfterResult { get; private set; }
  public Exception? ErrorException { get; private set; }
  public IMutationContext? CapturedContext { get; private set; }

  protected override ValueTask OnBeforeExecuteAsync(
      TestMutationCommand command,
      IMutationContext context,
      CancellationToken ct) {
    OnBeforeExecuteCalled = true;
    BeforeCommand = command;
    CapturedContext = context;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestMutationCommand command,
      TestMutationResult result,
      IMutationContext context,
      CancellationToken ct) {
    OnAfterExecuteCalled = true;
    AfterResult = result;
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestMutationResult?> OnErrorAsync(
      TestMutationCommand command,
      Exception ex,
      IMutationContext context,
      CancellationToken ct) {
    OnErrorCalled = true;
    ErrorException = ex;
    return ValueTask.FromResult(FallbackResult);
  }

  protected override ValueTask<TestMutationResult> DispatchCommandAsync(
      TestMutationCommand command,
      CancellationToken ct) {
    LastDispatchedCommand = command;
    LastCancellationToken = ct;

    if (ShouldThrowOnDispatch) {
      throw new InvalidOperationException("Test exception");
    }

    return ValueTask.FromResult(new TestMutationResult {
      Success = true,
      Value = $"{command.Value}_processed"
    });
  }

  // Expose protected methods for testing
  public ValueTask<TestMutationResult> TestExecuteAsync(TestMutationCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);

  public ValueTask<TestMutationResult> TestExecuteWithRequestAsync<TRequest>(TRequest request, CancellationToken ct)
      where TRequest : notnull
      => ExecuteWithRequestAsync(request, ct);
}

/// <summary>
/// Test implementation with custom request mapping.
/// </summary>
public class TestGraphQLMutationWithMapping : GraphQLMutationBase<TestMutationCommand, TestMutationResult> {
  public bool MapRequestToCommandCalled { get; private set; }

  protected override ValueTask<TestMutationCommand> MapRequestToCommandAsync<TRequest>(
      TRequest request,
      CancellationToken ct) {
    MapRequestToCommandCalled = true;
    var testRequest = request as TestMutationRequest;
    return ValueTask.FromResult(new TestMutationCommand { Value = testRequest!.Input });
  }

  protected override ValueTask<TestMutationResult> DispatchCommandAsync(
      TestMutationCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestMutationResult {
      Success = true,
      Value = $"{command.Value}_processed"
    });
  }

  public ValueTask<TestMutationResult> TestExecuteWithRequestAsync<TRequest>(TRequest request, CancellationToken ct)
      where TRequest : notnull
      => ExecuteWithRequestAsync(request, ct);
}

/// <summary>
/// Test implementation that uses context items.
/// </summary>
public class TestGraphQLMutationWithContextItems : GraphQLMutationBase<TestMutationCommand, TestMutationResult> {
  private const string TEST_KEY = "test-key";
  public bool ItemsAccessedSuccessfully { get; private set; }

  protected override ValueTask OnBeforeExecuteAsync(
      TestMutationCommand command,
      IMutationContext context,
      CancellationToken ct) {
    context.Items[TEST_KEY] = "test-value";
    return ValueTask.CompletedTask;
  }

  protected override ValueTask OnAfterExecuteAsync(
      TestMutationCommand command,
      TestMutationResult result,
      IMutationContext context,
      CancellationToken ct) {
    ItemsAccessedSuccessfully = context.Items.TryGetValue(TEST_KEY, out var value)
        && value as string == "test-value";
    return ValueTask.CompletedTask;
  }

  protected override ValueTask<TestMutationResult> DispatchCommandAsync(
      TestMutationCommand command,
      CancellationToken ct) {
    return ValueTask.FromResult(new TestMutationResult { Success = true, Value = command.Value });
  }

  public ValueTask<TestMutationResult> TestExecuteAsync(TestMutationCommand command, CancellationToken ct)
      => ExecuteAsync(command, ct);
}

#endregion
