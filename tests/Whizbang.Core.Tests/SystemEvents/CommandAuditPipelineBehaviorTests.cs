using System.Text.Json;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for CommandAuditPipelineBehavior.
/// The behavior emits CommandAudited system events after commands are processed.
/// </summary>
[Category("SystemEvents")]
[Category("Pipeline")]
public class CommandAuditPipelineBehaviorTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullEmitter_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions());

    // Act & Assert
    await Assert.That(() => new CommandAuditPipelineBehavior<TestCommand, string>(null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();

    // Act & Assert
    await Assert.That(() => new CommandAuditPipelineBehavior<TestCommand, string>(emitter, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region HandleAsync Tests

  [Test]
  public async Task HandleAsync_ExecutesContinuation_ReturnsResultAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options);

    var command = new TestCommand { OrderId = "ABC123" };
    const string expectedResult = "Success";

    // Act
    var result = await behavior.HandleAsync(command, () => Task.FromResult(expectedResult));

    // Assert
    await Assert.That(result).IsEqualTo(expectedResult);
  }

  [Test]
  public async Task HandleAsync_WhenCommandAuditDisabled_DoesNotEmitAuditAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions()); // CommandAuditEnabled = false
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - No audit emitted
    await Assert.That(emitter.EmitCommandAuditedCalls).IsEmpty();
  }

  [Test]
  public async Task HandleAsync_WhenCommandAuditEnabled_EmitsAuditEventAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Audit was emitted
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
  }

  [Test]
  public async Task HandleAsync_WithExcludedCommand_DoesNotEmitAuditAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    emitter.ExcludeTypes.Add(typeof(ExcludedCommand));
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<ExcludedCommand, string>(emitter, options);

    var command = new ExcludedCommand { Name = "Test" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - No audit emitted (excluded)
    await Assert.That(emitter.EmitCommandAuditedCalls).IsEmpty();
  }

  [Test]
  public async Task HandleAsync_PassesCommandAndResponseToEmitterAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options);

    var command = new TestCommand { OrderId = "ABC123" };
    const string response = "Success-123";

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult(response));

    // Assert
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.Command).IsEqualTo(command);
    await Assert.That(call.Response).IsEqualTo(response);
  }

  #endregion

  #region ReceptorName Extraction Tests

  [Test]
  public async Task HandleAsync_WithContext_PassesContextToEmitterAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var context = MessageContext.New();
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options, context);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Context was passed to emitter
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.Context).IsEqualTo(context);
  }

  [Test]
  public async Task HandleAsync_WithoutContext_GeneratesReceptorNameFromCommandTypeAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<CreateOrderCommand, string>(emitter, options);

    var command = new CreateOrderCommand { Amount = 100m };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - "CreateOrderCommand" -> "CreateOrderReceptor"
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("CreateOrderReceptor");
  }

  [Test]
  public async Task HandleAsync_WithReceptorNameInContext_UsesContextValueAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var context = new MockMessageContext { MetadataDict = { ["ReceptorName"] = "CustomReceptor" } };
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options, context);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Uses metadata value, not generated name
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("CustomReceptor");
  }

  [Test]
  public async Task HandleAsync_WithEmptyReceptorNameInContext_FallsBackToGeneratedAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var context = new MockMessageContext { MetadataDict = { ["ReceptorName"] = "" } };
    var behavior = new CommandAuditPipelineBehavior<CreateOrderCommand, string>(emitter, options, context);

    var command = new CreateOrderCommand { Amount = 100m };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Falls back to generated name due to empty string
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("CreateOrderReceptor");
  }

  [Test]
  public async Task HandleAsync_WithNonStringReceptorNameInContext_FallsBackToGeneratedAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var context = new MockMessageContext { MetadataDict = { ["ReceptorName"] = 12345 } };
    var behavior = new CommandAuditPipelineBehavior<CreateOrderCommand, string>(emitter, options, context);

    var command = new CreateOrderCommand { Amount = 100m };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Falls back to generated name due to non-string value
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("CreateOrderReceptor");
  }

  [Test]
  public async Task HandleAsync_WithCommandEndingInCommand_RemovesSuffixAndAppendsReceptorAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - "TestCommand" ends in "Command", so becomes "TestReceptor"
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("TestReceptor");
  }

  [Test]
  public async Task HandleAsync_WithCommandNotEndingInCommand_AppendsReceptorAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<PlaceOrder, string>(emitter, options);

    var command = new PlaceOrder { Amount = 100m };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - "PlaceOrder" doesn't end in "Command", so becomes "PlaceOrderReceptor"
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.ReceptorName).IsEqualTo("PlaceOrderReceptor");
  }

  [Test]
  public async Task HandleAsync_WithNullContext_StillEmitsAuditAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var behavior = new CommandAuditPipelineBehavior<TestCommand, string>(emitter, options, context: null);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await behavior.HandleAsync(command, () => Task.FromResult("result"));

    // Assert - Audit was emitted even without context
    await Assert.That(emitter.EmitCommandAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitCommandAuditedCalls[0];
    await Assert.That(call.Context).IsNull();
  }

  #endregion

  #region Test Types

  private sealed record TestCommand {
    public required string OrderId { get; init; }
  }

  private sealed record CreateOrderCommand {
    public required decimal Amount { get; init; }
  }

  [AuditEvent(Exclude = true)]
  private sealed record ExcludedCommand {
    public required string Name { get; init; }
  }

  // Command that doesn't end with "Command" suffix
  private sealed record PlaceOrder {
    public required decimal Amount { get; init; }
  }

  #endregion

  #region Mock Implementations

  private sealed class MockMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = CorrelationId.New();
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; init; }

    public Dictionary<string, object> MetadataDict { get; } = [];
    public IReadOnlyDictionary<string, object> Metadata => MetadataDict;
  }

  private sealed class MockSystemEventEmitter : ISystemEventEmitter {
    public List<(object Command, object Response, string ReceptorName, IMessageContext? Context)> EmitCommandAuditedCalls { get; } = [];
    public HashSet<Type> ExcludeTypes { get; } = [];

    public Task EmitEventAuditedAsync<TEvent>(Guid streamId, long streamPosition, MessageEnvelope<TEvent> envelope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(TCommand command, TResponse response, string receptorName, IMessageContext? context, CancellationToken cancellationToken = default)
        where TCommand : notnull {
      EmitCommandAuditedCalls.Add((command!, response!, receptorName, context));
      return Task.CompletedTask;
    }

    public Task EmitAsync<TSystemEvent>(TSystemEvent systemEvent, CancellationToken cancellationToken = default)
        where TSystemEvent : ISystemEvent =>
        Task.CompletedTask;

    public bool ShouldExcludeFromAudit(Type type) => ExcludeTypes.Contains(type);
  }

  #endregion
}
