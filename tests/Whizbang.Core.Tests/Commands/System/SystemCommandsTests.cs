using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Commands.System;

/// <summary>
/// Tests for system commands in <see cref="Whizbang.Core.Commands.System"/>.
/// </summary>
public class SystemCommandsTests {
  // === RebuildPerspectiveCommand Tests ===

  [Test]
  public async Task RebuildPerspectiveCommand_WithPerspectiveNames_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new RebuildPerspectiveCommand(["OrderSummary"]);

    // Assert
    await Assert.That(command.PerspectiveNames).IsNotNull();
    await Assert.That(command.PerspectiveNames![0]).IsEqualTo("OrderSummary");
    await Assert.That(command.FromEventId).IsNull();
    await Assert.That(command.Mode).IsEqualTo(RebuildMode.BlueGreen);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithFromEventId_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new RebuildPerspectiveCommand(["OrderSummary"], FromEventId: 12345L);

    // Assert
    await Assert.That(command.PerspectiveNames![0]).IsEqualTo("OrderSummary");
    await Assert.That(command.FromEventId).IsEqualTo(12345L);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_DefaultParameters_AllNullAsync() {
    // Arrange & Act
    var command = new RebuildPerspectiveCommand();

    // Assert
    await Assert.That(command.PerspectiveNames).IsNull();
    await Assert.That(command.Mode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(command.IncludeStreamIds).IsNull();
    await Assert.That(command.ExcludeStreamIds).IsNull();
    await Assert.That(command.FromEventId).IsNull();
  }

  [Test]
  public async Task RebuildPerspectiveCommand_ImplementsICommandAsync() {
    // Arrange
    var command = new RebuildPerspectiveCommand(["Test"]);

    // Assert
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  [Test]
  public async Task RebuildPerspectiveCommand_Equality_SameReferenceArrayMatchesAsync() {
    // Arrange — record equality with arrays checks reference equality for array fields
    var names = new[] { "OrderSummary" };
    var command1 = new RebuildPerspectiveCommand(names, FromEventId: 100L);
    var command2 = new RebuildPerspectiveCommand(names, FromEventId: 100L);

    // Assert
    await Assert.That(command1).IsEqualTo(command2);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_DifferentMode_NotEqualAsync() {
    // Arrange
    var command1 = new RebuildPerspectiveCommand(Mode: RebuildMode.BlueGreen);
    var command2 = new RebuildPerspectiveCommand(Mode: RebuildMode.InPlace);

    // Assert
    await Assert.That(command1).IsNotEqualTo(command2);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithInPlaceMode_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new RebuildPerspectiveCommand(["Test"], Mode: RebuildMode.InPlace);

    // Assert
    await Assert.That(command.Mode).IsEqualTo(RebuildMode.InPlace);
  }

  [Test]
  public async Task CancelPerspectiveRebuildCommand_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new CancelPerspectiveRebuildCommand("OrderSummary");

    // Assert
    await Assert.That(command.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  // === ClearCacheCommand Tests ===

  [Test]
  public async Task ClearCacheCommand_DefaultParameters_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new ClearCacheCommand();

    // Assert
    await Assert.That(command.CacheKey).IsNull();
    await Assert.That(command.CacheRegion).IsNull();
  }

  [Test]
  public async Task ClearCacheCommand_WithCacheKey_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new ClearCacheCommand(CacheKey: "user:123");

    // Assert
    await Assert.That(command.CacheKey).IsEqualTo("user:123");
    await Assert.That(command.CacheRegion).IsNull();
  }

  [Test]
  public async Task ClearCacheCommand_WithAllParameters_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new ClearCacheCommand("user:123", "users");

    // Assert
    await Assert.That(command.CacheKey).IsEqualTo("user:123");
    await Assert.That(command.CacheRegion).IsEqualTo("users");
  }

  [Test]
  public async Task ClearCacheCommand_ImplementsICommandAsync() {
    // Arrange
    var command = new ClearCacheCommand();

    // Assert
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  // === DiagnosticsCommand Tests ===

  [Test]
  public async Task DiagnosticsCommand_WithHealthCheck_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new DiagnosticsCommand(DiagnosticType.HealthCheck);

    // Assert
    await Assert.That(command.Type).IsEqualTo(DiagnosticType.HealthCheck);
    await Assert.That(command.CorrelationId).IsNull();
  }

  [Test]
  public async Task DiagnosticsCommand_WithCorrelationId_CreatesCorrectlyAsync() {
    // Arrange
    var correlationId = Guid.NewGuid();

    // Act
    var command = new DiagnosticsCommand(DiagnosticType.Full, correlationId);

    // Assert
    await Assert.That(command.Type).IsEqualTo(DiagnosticType.Full);
    await Assert.That(command.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task DiagnosticsCommand_ImplementsICommandAsync() {
    // Arrange
    var command = new DiagnosticsCommand(DiagnosticType.ResourceMetrics);

    // Assert
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  [Test]
  [Arguments(DiagnosticType.HealthCheck)]
  [Arguments(DiagnosticType.ResourceMetrics)]
  [Arguments(DiagnosticType.PipelineStatus)]
  [Arguments(DiagnosticType.PerspectiveStatus)]
  [Arguments(DiagnosticType.Full)]
  public async Task DiagnosticsCommand_AllDiagnosticTypes_CreateCorrectlyAsync(DiagnosticType type) {
    // Arrange & Act
    var command = new DiagnosticsCommand(type);

    // Assert
    await Assert.That(command.Type).IsEqualTo(type);
  }

  // === DiagnosticType Enum Tests ===

  [Test]
  public async Task DiagnosticType_HasExpectedValuesAsync() {
    // Assert
    await Assert.That(Enum.IsDefined<DiagnosticType>(DiagnosticType.HealthCheck)).IsTrue();
    await Assert.That(Enum.IsDefined<DiagnosticType>(DiagnosticType.ResourceMetrics)).IsTrue();
    await Assert.That(Enum.IsDefined<DiagnosticType>(DiagnosticType.PipelineStatus)).IsTrue();
    await Assert.That(Enum.IsDefined<DiagnosticType>(DiagnosticType.PerspectiveStatus)).IsTrue();
    await Assert.That(Enum.IsDefined<DiagnosticType>(DiagnosticType.Full)).IsTrue();
  }

  // === PauseProcessingCommand Tests ===

  [Test]
  public async Task PauseProcessingCommand_DefaultParameters_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new PauseProcessingCommand();

    // Assert
    await Assert.That(command.DurationSeconds).IsNull();
    await Assert.That(command.Reason).IsNull();
  }

  [Test]
  public async Task PauseProcessingCommand_WithDuration_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new PauseProcessingCommand(DurationSeconds: 300);

    // Assert
    await Assert.That(command.DurationSeconds).IsEqualTo(300);
    await Assert.That(command.Reason).IsNull();
  }

  [Test]
  public async Task PauseProcessingCommand_WithAllParameters_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new PauseProcessingCommand(600, "Scheduled maintenance");

    // Assert
    await Assert.That(command.DurationSeconds).IsEqualTo(600);
    await Assert.That(command.Reason).IsEqualTo("Scheduled maintenance");
  }

  [Test]
  public async Task PauseProcessingCommand_ImplementsICommandAsync() {
    // Arrange
    var command = new PauseProcessingCommand();

    // Assert
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  // === ResumeProcessingCommand Tests ===

  [Test]
  public async Task ResumeProcessingCommand_DefaultParameters_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new ResumeProcessingCommand();

    // Assert
    await Assert.That(command.Reason).IsNull();
  }

  [Test]
  public async Task ResumeProcessingCommand_WithReason_CreatesCorrectlyAsync() {
    // Arrange & Act
    var command = new ResumeProcessingCommand("Maintenance complete");

    // Assert
    await Assert.That(command.Reason).IsEqualTo("Maintenance complete");
  }

  [Test]
  public async Task ResumeProcessingCommand_ImplementsICommandAsync() {
    // Arrange
    var command = new ResumeProcessingCommand();

    // Assert
    await Assert.That(command).IsAssignableTo<ICommand>();
  }

  // === JSON Serialization Tests ===

  [Test]
  public async Task RebuildPerspectiveCommand_SerializesCorrectlyAsync() {
    // Arrange
    var command = new RebuildPerspectiveCommand(["TestPerspective"], FromEventId: 42L);

    // Act
    var json = JsonSerializer.Serialize(command);
    var deserialized = JsonSerializer.Deserialize<RebuildPerspectiveCommand>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.PerspectiveNames![0]).IsEqualTo("TestPerspective");
    await Assert.That(deserialized.FromEventId).IsEqualTo(42L);
  }

  [Test]
  public async Task DiagnosticsCommand_SerializesCorrectlyAsync() {
    // Arrange
    var correlationId = Guid.NewGuid();
    var command = new DiagnosticsCommand(DiagnosticType.Full, correlationId);

    // Act
    var json = JsonSerializer.Serialize(command);
    var deserialized = JsonSerializer.Deserialize<DiagnosticsCommand>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Type).IsEqualTo(DiagnosticType.Full);
    await Assert.That(deserialized.CorrelationId).IsEqualTo(correlationId);
  }
}
