using Whizbang.Migrate.Commands;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Tests for the apply command that transforms code patterns.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/ApplyCommand.cs:*</tests>
public class ApplyCommandTests {
  [Test]
  public async Task ExecuteAsync_TransformsHandlers_ReturnsTransformationResultAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Handler.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """);

      var command = new ApplyCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsGreaterThan(0);

      // Verify file was transformed
      var transformedContent = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformedContent).Contains("IReceptor<");
      await Assert.That(transformedContent).DoesNotContain("IHandle<");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_TransformsProjections_ReturnsTransformationResultAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Projection.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Marten.Events.Aggregation;

        public class OrderProjection : SingleStreamProjection<Order> {
          public void Apply(OrderCreated @event, Order state) { }
        }

        public class Order { }
        public record OrderCreated(string Id);
        """);

      var command = new ApplyCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsGreaterThan(0);

      // Verify file was transformed
      var transformedContent = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformedContent).Contains("IPerspectiveFor<");
      await Assert.That(transformedContent).DoesNotContain("SingleStreamProjection<");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_DryRunMode_DoesNotModifyFilesAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Handler.cs");
      var originalContent = """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """;
      await File.WriteAllTextAsync(sourceFile, originalContent);

      var command = new ApplyCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir, dryRun: true);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsGreaterThan(0);

      // Verify file was NOT modified
      var currentContent = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(currentContent).IsEqualTo(originalContent);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_NonExistentDirectory_ReturnsFailureAsync() {
    // Arrange
    var command = new ApplyCommand();
    var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

    // Act
    var result = await command.ExecuteAsync(nonExistentPath);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorMessage).Contains("not found");
  }

  [Test]
  public async Task ExecuteAsync_NoMigratablePatterns_ReturnsZeroTransformationsAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Service.cs");
      await File.WriteAllTextAsync(sourceFile, """
        public class OrderService {
          public void Process() { }
        }
        """);

      var command = new ApplyCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsEqualTo(0);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_TracksAllChanges_ReturnsChangeLogAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      await File.WriteAllTextAsync(Path.Combine(tempDir, "Handler.cs"), """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """);

      var command = new ApplyCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.Changes.Count).IsGreaterThan(0);
      await Assert.That(result.Changes.Any(c => c.FilePath.EndsWith("Handler.cs", StringComparison.Ordinal))).IsTrue();
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }
}
