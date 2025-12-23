using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Pipeline;

namespace Whizbang.Core.Tests.Pipeline;

/// <summary>
/// Tests for PipelineBehavior base class.
/// </summary>
[Category("Pipeline")]
public class PipelineBehaviorTests {
  [Test]
  public async Task ExecuteNextAsync_ShouldInvokeNextDelegateAsync() {
    // Arrange
    var behavior = new TestPipelineBehavior();
    var nextCalled = false;
    Task<string> next() {
      nextCalled = true;
      return Task.FromResult("result");
    }

    // Act
    var result = await behavior.HandleAsync("request", next);

    // Assert
    await Assert.That(nextCalled).IsTrue();
    await Assert.That(result).IsEqualTo("result-processed");
  }

  [Test]
  public async Task Handle_CanPreProcessRequestAsync() {
    // Arrange
    var behavior = new PreProcessingBehavior();

    Task<string> next() {
      behavior.Log.Add("handler");
      return Task.FromResult("result");
    }

    // Act
    await behavior.HandleAsync("request", next);

    // Assert
    await Assert.That(behavior.Log).Count().IsEqualTo(2);
    await Assert.That(behavior.Log[0]).IsEqualTo("pre-process");
    await Assert.That(behavior.Log[1]).IsEqualTo("handler");
  }

  [Test]
  public async Task Handle_CanPostProcessResponseAsync() {
    // Arrange
    var behavior = new PostProcessingBehavior();

    static Task<string> next() {
      return Task.FromResult("original");
    }

    // Act
    var result = await behavior.HandleAsync("request", next);

    // Assert
    await Assert.That(result).IsEqualTo("original-modified");
  }

  [Test]
  public async Task Handle_CanShortCircuitPipelineAsync() {
    // Arrange
    var behavior = new ShortCircuitBehavior();
    var nextCalled = false;

    Task<string> next() {
      nextCalled = true;
      return Task.FromResult("should-not-be-called");
    }

    // Act
    var result = await behavior.HandleAsync("request", next);

    // Assert
    await Assert.That(nextCalled).IsFalse();
    await Assert.That(result).IsEqualTo("short-circuited");
  }

  // Test behaviors

  private sealed class TestPipelineBehavior : PipelineBehavior<string, string> {
    public override async Task<string> HandleAsync(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      var result = await ExecuteNextAsync(next);
      return $"{result}-processed";
    }
  }

  private sealed class PreProcessingBehavior : PipelineBehavior<string, string> {
    private readonly List<string> _log = [];

    public List<string> Log => _log;

    public override async Task<string> HandleAsync(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      _log.Add("pre-process");
      return await next();
    }
  }

  private sealed class PostProcessingBehavior : PipelineBehavior<string, string> {
    public override async Task<string> HandleAsync(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      var result = await next();
      return $"{result}-modified";
    }
  }

  private sealed class ShortCircuitBehavior : PipelineBehavior<string, string> {
    public override Task<string> HandleAsync(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      // Short-circuit - don't call next
      return Task.FromResult("short-circuited");
    }
  }
}
