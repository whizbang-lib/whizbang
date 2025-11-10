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
    var result = await behavior.Handle("request", next);

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
    await behavior.Handle("request", next);

    // Assert
    await Assert.That(behavior.Log).HasCount().EqualTo(2);
    await Assert.That(behavior.Log[0]).IsEqualTo("pre-process");
    await Assert.That(behavior.Log[1]).IsEqualTo("handler");
  }

  [Test]
  public async Task Handle_CanPostProcessResponseAsync() {
    // Arrange
    var behavior = new PostProcessingBehavior();

    Task<string> next() {
      return Task.FromResult("original");
    }

    // Act
    var result = await behavior.Handle("request", next);

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
    var result = await behavior.Handle("request", next);

    // Assert
    await Assert.That(nextCalled).IsFalse();
    await Assert.That(result).IsEqualTo("short-circuited");
  }

  // Test behaviors

  private class TestPipelineBehavior : PipelineBehavior<string, string> {
    public override async Task<string> Handle(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      var result = await ExecuteNextAsync(next);
      return $"{result}-processed";
    }
  }

  private class PreProcessingBehavior : PipelineBehavior<string, string> {
    private readonly List<string> _log = new();

    public List<string> Log => _log;

    public override async Task<string> Handle(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      _log.Add("pre-process");
      return await next();
    }
  }

  private class PostProcessingBehavior : PipelineBehavior<string, string> {
    public override async Task<string> Handle(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      var result = await next();
      return $"{result}-modified";
    }
  }

  private class ShortCircuitBehavior : PipelineBehavior<string, string> {
    public override Task<string> Handle(
      string request,
      Func<Task<string>> next,
      CancellationToken cancellationToken = default
    ) {
      // Short-circuit - don't call next
      return Task.FromResult("short-circuited");
    }
  }
}
