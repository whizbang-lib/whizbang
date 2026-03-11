using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for automatic StreamId generation in the Dispatcher's _createEnvelope path.
/// Verifies that [GenerateStreamId] on commands (and any IMessage) triggers auto-generation
/// via the same plumbing as [Populate*] attributes.
/// </summary>
[Category("Dispatcher")]
[Category("StreamId")]
[Category("GenerateStreamId")]
[NotInParallel("StreamIdExtractorRegistry")]
public class DispatcherStreamIdGenerationTests {

  // ========================================
  // Test Message Types
  // ========================================

  /// <summary>
  /// Command with [GenerateStreamId] and IHasStreamId - should get StreamId auto-generated.
  /// </summary>
  public class GenerateStreamIdCommand : ICommand, IHasStreamId {
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }

    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Command with [GenerateStreamId(OnlyIfEmpty = true)] - only generates if empty.
  /// </summary>
  public class GenerateStreamIdOnlyIfEmptyCommand : ICommand, IHasStreamId {
    [StreamId]
    [GenerateStreamId(OnlyIfEmpty = true)]
    public Guid StreamId { get; set; }

    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Event with [GenerateStreamId] and IHasStreamId - should get StreamId auto-generated in _createEnvelope path.
  /// </summary>
  public class GenerateStreamIdEvent : IEvent, IHasStreamId {
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }

    public string Data { get; set; } = "";
  }

  /// <summary>
  /// Command with [StreamId] but NO [GenerateStreamId] - should NOT auto-generate.
  /// </summary>
  public class NoGenerateStreamIdCommand : ICommand, IHasStreamId {
    [StreamId]
    public Guid StreamId { get; set; }

    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Base command with [StreamId] but NOT IHasStreamId - simulates JDNext's BaseJdxCommand pattern.
  /// </summary>
  public class BaseTestCommand : ICommand {
    [StreamId]
    public virtual Guid StreamId { get; set; }
  }

  /// <summary>
  /// Derived command with [GenerateStreamId] at class level, inheriting [StreamId] from base.
  /// Simulates JDNext's LoginAttemptCommand : BaseJdxCommand pattern.
  /// Does NOT implement IHasStreamId - uses SetStreamId fallback.
  /// </summary>
  [GenerateStreamId]
  public class InheritedStreamIdCommand : BaseTestCommand {
    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Derived command with [GenerateStreamId(OnlyIfEmpty = true)] at class level, inheriting [StreamId] from base.
  /// Does NOT implement IHasStreamId.
  /// </summary>
  [GenerateStreamId(OnlyIfEmpty = true)]
  public class InheritedOnlyIfEmptyCommand : BaseTestCommand {
    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Command without IHasStreamId and without [StreamId] - should be skipped entirely.
  /// </summary>
  public record SimpleCommand(string Data) : ICommand;

  /// <summary>Response for SimpleCommand</summary>
  public record SimpleResponse(string Result);

  /// <summary>Response for commands that return a StreamId</summary>
  public record StreamIdResponse(Guid StreamId);

  // ========================================
  // Test Receptors
  // ========================================

  public class GenerateStreamIdCommandReceptor : IReceptor<GenerateStreamIdCommand, StreamIdResponse> {
    public ValueTask<StreamIdResponse> HandleAsync(GenerateStreamIdCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new StreamIdResponse(message.StreamId));
    }
  }

  public class GenerateStreamIdOnlyIfEmptyCommandReceptor : IReceptor<GenerateStreamIdOnlyIfEmptyCommand, StreamIdResponse> {
    public ValueTask<StreamIdResponse> HandleAsync(GenerateStreamIdOnlyIfEmptyCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new StreamIdResponse(message.StreamId));
    }
  }

  public class NoGenerateStreamIdCommandReceptor : IReceptor<NoGenerateStreamIdCommand, StreamIdResponse> {
    public ValueTask<StreamIdResponse> HandleAsync(NoGenerateStreamIdCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new StreamIdResponse(message.StreamId));
    }
  }

  public class SimpleCommandReceptor : IReceptor<SimpleCommand, SimpleResponse> {
    public ValueTask<SimpleResponse> HandleAsync(SimpleCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new SimpleResponse("ok"));
    }
  }

  public class InheritedStreamIdCommandReceptor : IReceptor<InheritedStreamIdCommand, StreamIdResponse> {
    public ValueTask<StreamIdResponse> HandleAsync(InheritedStreamIdCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new StreamIdResponse(message.StreamId));
    }
  }

  public class InheritedOnlyIfEmptyCommandReceptor : IReceptor<InheritedOnlyIfEmptyCommand, StreamIdResponse> {
    public ValueTask<StreamIdResponse> HandleAsync(InheritedOnlyIfEmptyCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new StreamIdResponse(message.StreamId));
    }
  }

  // ========================================
  // Tests: Command with [GenerateStreamId]
  // ========================================

  [Test]
  public async Task SendAsync_CommandWithGenerateStreamId_AutoGeneratesStreamIdAsync() {
    // Arrange
    var command = new GenerateStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - StreamId should have been auto-generated (non-empty)
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task SendAsync_CommandWithGenerateStreamId_OverwritesExistingStreamIdAsync() {
    // Arrange - command starts with a pre-set StreamId
    var existingId = Guid.NewGuid();
    var command = new GenerateStreamIdCommand { StreamId = existingId, Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should overwrite because OnlyIfEmpty defaults to false
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(result.StreamId).IsNotEqualTo(existingId);
  }

  [Test]
  public async Task SendAsync_CommandWithGenerateStreamId_StreamIdAppearsInHopMetadataAsync() {
    // Arrange
    var command = new GenerateStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - The hop metadata should contain the generated StreamId
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsNotEqualTo(Guid.Empty);
    // The StreamId in the receipt should match the one that was generated on the command
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(command.StreamId);
  }

  // ========================================
  // Tests: OnlyIfEmpty = true
  // ========================================

  [Test]
  public async Task SendAsync_OnlyIfEmpty_GeneratesWhenStreamIdIsEmptyAsync() {
    // Arrange - StreamId starts as Guid.Empty
    var command = new GenerateStreamIdOnlyIfEmptyCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should generate because StreamId was empty
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task SendAsync_OnlyIfEmpty_PreservesExistingStreamIdAsync() {
    // Arrange - StreamId starts with a real value
    var existingId = Guid.NewGuid();
    var command = new GenerateStreamIdOnlyIfEmptyCommand { StreamId = existingId, Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should NOT overwrite because OnlyIfEmpty = true and StreamId was already set
    await Assert.That(result.StreamId).IsEqualTo(existingId);
  }

  // ========================================
  // Tests: No [GenerateStreamId]
  // ========================================

  [Test]
  public async Task SendAsync_CommandWithoutGenerateStreamId_DoesNotAutoGenerateAsync() {
    // Arrange - Command has [StreamId] but no [GenerateStreamId]
    var command = new NoGenerateStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - StreamId should remain empty (no auto-generation)
    await Assert.That(result.StreamId).IsEqualTo(Guid.Empty);
  }

  // ========================================
  // Tests: Not IHasStreamId
  // ========================================

  [Test]
  public async Task SendAsync_CommandWithoutIHasStreamId_SkipsGenerationAsync() {
    // Arrange - SimpleCommand is a record (not IHasStreamId)
    var command = new SimpleCommand("test");
    var dispatcher = _createDispatcher();

    // Act - Should complete without error (auto-generation is skipped)
    var result = await dispatcher.LocalInvokeAsync<SimpleResponse>(command);

    // Assert
    await Assert.That(result.Result).IsEqualTo("ok");
  }

  // ========================================
  // Tests: Null extractor
  // ========================================

  [Test]
  public async Task SendAsync_NullStreamIdExtractor_SkipsGenerationAsync() {
    // Arrange - Create dispatcher without any IStreamIdExtractor
    var command = new GenerateStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcherWithoutExtractor();

    // Act - Should not throw (gracefully skips generation)
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - StreamId should remain empty (no extractor to check)
    await Assert.That(result.StreamId).IsEqualTo(Guid.Empty);
  }

  // ========================================
  // Tests: Event with [GenerateStreamId] in _createEnvelope path
  // ========================================

  [Test]
  public async Task PublishAsync_EventWithGenerateStreamId_AutoGeneratesStreamIdAsync() {
    // Arrange
    var @event = new GenerateStreamIdEvent { Data = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.PublishAsync(@event);

    // Assert - StreamId should be auto-generated
    await Assert.That(@event.StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(@event.StreamId);
  }

  // ========================================
  // Tests: SendAsync (non-generic via DeliveryReceipt path)
  // ========================================

  [Test]
  public async Task SendAsync_ViaDeliveryReceipt_GeneratesStreamIdAsync() {
    // Arrange
    var command = new GenerateStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Tests: Inherited [StreamId] without IHasStreamId (SetStreamId fallback)
  // ========================================

  [Test]
  public async Task SendAsync_InheritedStreamId_WithoutIHasStreamId_AutoGeneratesViaSetStreamIdAsync() {
    // Arrange - Simulates JDNext's LoginAttemptCommand : BaseJdxCommand pattern
    // [GenerateStreamId] on class, [StreamId] inherited from base, no IHasStreamId
    var command = new InheritedStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - StreamId should be auto-generated via SetStreamId fallback
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task SendAsync_InheritedStreamId_OverwritesExistingAsync() {
    // Arrange - pre-set StreamId, OnlyIfEmpty defaults to false
    var existingId = Guid.NewGuid();
    var command = new InheritedStreamIdCommand { StreamId = existingId, Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should overwrite because OnlyIfEmpty is false
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(result.StreamId).IsNotEqualTo(existingId);
  }

  [Test]
  public async Task SendAsync_InheritedStreamId_OnlyIfEmpty_GeneratesWhenEmptyAsync() {
    // Arrange
    var command = new InheritedOnlyIfEmptyCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should generate because StreamId was empty
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task SendAsync_InheritedStreamId_OnlyIfEmpty_PreservesExistingAsync() {
    // Arrange - StreamId starts with a real value
    var existingId = Guid.NewGuid();
    var command = new InheritedOnlyIfEmptyCommand { StreamId = existingId, Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<StreamIdResponse>(command);

    // Assert - Should NOT overwrite because OnlyIfEmpty = true
    await Assert.That(result.StreamId).IsEqualTo(existingId);
  }

  [Test]
  public async Task SendAsync_InheritedStreamId_StreamIdAppearsInReceiptAsync() {
    // Arrange
    var command = new InheritedStreamIdCommand { Description = "test" };
    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Receipt should contain the generated StreamId
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(command.StreamId);
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

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private static IDispatcher _createDispatcherWithoutExtractor() {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    // Remove IStreamIdExtractor registration
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IStreamIdExtractor));
    if (descriptor != null) {
      services.Remove(descriptor);
    }

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
