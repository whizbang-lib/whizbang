using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for Dispatcher integration with the message tag processing system.
/// Verifies that the Dispatcher invokes IMessageTagProcessor after successful receptor completion.
/// </summary>
public class DispatcherTagProcessingTests {
  // Test command and response
  public record TestCommand(Guid Id, string Data);
  public record TestResult(Guid Id, bool Processed);

  // Test receptor that returns a result
  public class TestCommandReceptor : IReceptor<TestCommand, TestResult> {
    public static int InvocationCount { get; private set; }
    public static TestCommand? LastCommand { get; private set; }

    public static void Reset() {
      InvocationCount = 0;
      LastCommand = null;
    }

    public ValueTask<TestResult> HandleAsync(TestCommand message, CancellationToken cancellationToken = default) {
      InvocationCount++;
      LastCommand = message;
      return ValueTask.FromResult(new TestResult(message.Id, true));
    }
  }

  // Spy implementation of IMessageTagProcessor to track invocations
  public class SpyMessageTagProcessor : IMessageTagProcessor {
    public int InvocationCount { get; private set; }
    public object? LastMessage { get; private set; }
    public Type? LastMessageType { get; private set; }
    public LifecycleStage? LastStage { get; private set; }
    public IScopeContext? LastScope { get; private set; }
    public List<(object Message, Type MessageType, LifecycleStage Stage)> AllInvocations { get; } = [];

    public void Reset() {
      InvocationCount = 0;
      LastMessage = null;
      LastMessageType = null;
      LastStage = null;
      LastScope = null;
      AllInvocations.Clear();
    }

    public ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      InvocationCount++;
      LastMessage = message;
      LastMessageType = messageType;
      LastStage = stage;
      LastScope = scope;
      AllInvocations.Add((message, messageType, stage));
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithTagProcessingEnabled_InvokesTagProcessorAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act
    await dispatcher.LocalInvokeAsync<TestResult>(command);

    // Assert - Tag processor should be invoked
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(1);
    await Assert.That(spyProcessor.LastMessage).IsEqualTo(command);
    await Assert.That(spyProcessor.LastMessageType).IsEqualTo(typeof(TestCommand));
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithTagProcessingDisabled_SkipsTagProcessorAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = false; // Disabled
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act
    await dispatcher.LocalInvokeAsync<TestResult>(command);

    // Assert - Tag processor should NOT be invoked
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(0);
    await Assert.That(spyProcessor.LastMessage).IsNull();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithLifecycleStageMode_SkipsImmediateProcessingAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AsLifecycleStage; // Different mode
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act
    await dispatcher.LocalInvokeAsync<TestResult>(command);

    // Assert - Immediate processing should be skipped when using lifecycle stage mode
    // (Tag processing happens during lifecycle invocation instead)
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(0);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithNoTagProcessor_DoesNotThrowAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    // Create dispatcher WITHOUT registering IMessageTagProcessor
    var dispatcher = _createDispatcherWithoutProcessor(options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act & Assert - Should not throw even without a processor
    var result = await dispatcher.LocalInvokeAsync<TestResult>(command);
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Processed).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithTagProcessingEnabled_InvokesTagProcessorAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Tag processor should be invoked
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(1);
    await Assert.That(spyProcessor.LastMessage).IsEqualTo(command);
    await Assert.That(spyProcessor.LastMessageType).IsEqualTo(typeof(TestCommand));
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithTagProcessingDisabled_SkipsTagProcessorAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = false; // Disabled
    });
    var command = new TestCommand(Guid.CreateVersion7(), "Test");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Tag processor should NOT be invoked
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(0);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_MultipleCommands_ProcessesTagsForEachAsync() {
    // Arrange
    TestCommandReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessor(spyProcessor, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command1 = new TestCommand(Guid.CreateVersion7(), "Test1");
    var command2 = new TestCommand(Guid.CreateVersion7(), "Test2");
    var command3 = new TestCommand(Guid.CreateVersion7(), "Test3");

    // Act
    await dispatcher.LocalInvokeAsync<TestResult>(command1);
    await dispatcher.LocalInvokeAsync<TestResult>(command2);
    await dispatcher.LocalInvokeAsync<TestResult>(command3);

    // Assert - Tag processor should be invoked for each command
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(3);
    await Assert.That(spyProcessor.AllInvocations.Count).IsEqualTo(3);
    await Assert.That(spyProcessor.AllInvocations[0].Message).IsEqualTo(command1);
    await Assert.That(spyProcessor.AllInvocations[1].Message).IsEqualTo(command2);
    await Assert.That(spyProcessor.AllInvocations[2].Message).IsEqualTo(command3);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_ReceptorThrows_DoesNotInvokeTagProcessorAsync() {
    // Arrange
    ThrowingReceptor.Reset();
    var spyProcessor = new SpyMessageTagProcessor();
    var dispatcher = _createDispatcherWithProcessorForThrowing(spyProcessor, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });
    var command = new ThrowingCommand(Guid.CreateVersion7());

    // Act & Assert - Receptor throws, tag processor should NOT be invoked
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<ThrowingResult>(command))
        .ThrowsExactly<InvalidOperationException>();
    await Assert.That(spyProcessor.InvocationCount).IsEqualTo(0);
  }

  // Command/result for throwing receptor test
  public record ThrowingCommand(Guid Id);
  public record ThrowingResult(Guid Id);

  // Receptor that always throws
  public class ThrowingReceptor : IReceptor<ThrowingCommand, ThrowingResult> {
    public static void Reset() { }

    public ValueTask<ThrowingResult> HandleAsync(ThrowingCommand message, CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Receptor failed");
    }
  }

  // Helper to create a dispatcher with a spy processor
  private static IDispatcher _createDispatcherWithProcessor(
      SpyMessageTagProcessor spyProcessor,
      Action<WhizbangCoreOptions>? configure = null) {
    var services = new ServiceCollection();

    // Register Whizbang with options
    services.AddWhizbang(options => {
      configure?.Invoke(options);
    });

    // Replace the registered IMessageTagProcessor with our spy
    // Remove the default registration and add our spy
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMessageTagProcessor));
    if (descriptor != null) {
      services.Remove(descriptor);
    }
    services.AddSingleton<IMessageTagProcessor>(spyProcessor);

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Helper to create a dispatcher without a tag processor
  private static IDispatcher _createDispatcherWithoutProcessor(Action<WhizbangCoreOptions>? configure = null) {
    var services = new ServiceCollection();

    // Register Whizbang with options
    services.AddWhizbang(options => {
      configure?.Invoke(options);
    });

    // Remove the IMessageTagProcessor registration to test null handling
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMessageTagProcessor));
    if (descriptor != null) {
      services.Remove(descriptor);
    }

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Helper for throwing receptor test (needs separate because of generated dispatcher lookup)
  private static IDispatcher _createDispatcherWithProcessorForThrowing(
      SpyMessageTagProcessor spyProcessor,
      Action<WhizbangCoreOptions>? configure = null) {
    // For now, use the same setup - the generated dispatcher should handle both
    return _createDispatcherWithProcessor(spyProcessor, configure);
  }
}
