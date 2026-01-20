using Whizbang.Migrate.Commands;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Tests for the analyze command that scans projects for migration patterns.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/AnalyzeCommand.cs:*</tests>
public class AnalyzeCommandTests {
  [Test]
  public async Task ExecuteAsync_FindsWolverineHandlers_ReportsHandlerCountAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-analyze-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      // Create a file with Wolverine handlers
      var sourceFile = Path.Combine(tempDir, "Handler.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public class UpdateOrderHandler : IHandle<UpdateOrderCommand> {
          public Task Handle(UpdateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        public record UpdateOrderCommand(string OrderId);
        """);

      var command = new AnalyzeCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.WolverineHandlerCount).IsEqualTo(2);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_FindsMartenProjections_ReportsProjectionCountAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-analyze-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      // Create a file with Marten projections
      var sourceFile = Path.Combine(tempDir, "Projection.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Marten.Events.Aggregation;

        public class OrderProjection : SingleStreamProjection<Order> {
          public void Apply(OrderCreated @event, Order state) { }
        }

        public class CustomerProjection : SingleStreamProjection<Customer> {
          public void Apply(CustomerCreated @event, Customer state) { }
        }

        public class Order { }
        public class Customer { }
        public record OrderCreated(string Id);
        public record CustomerCreated(string Id);
        """);

      var command = new AnalyzeCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.MartenProjectionCount).IsEqualTo(2);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_EmptyDirectory_ReturnsZeroCountsAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-analyze-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var command = new AnalyzeCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.WolverineHandlerCount).IsEqualTo(0);
      await Assert.That(result.MartenProjectionCount).IsEqualTo(0);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_NonExistentDirectory_ReturnsFailureAsync() {
    // Arrange
    var command = new AnalyzeCommand();
    var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

    // Act
    var result = await command.ExecuteAsync(nonExistentPath);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorMessage).Contains("not found");
  }

  [Test]
  public async Task ExecuteAsync_MixedPatterns_ReportsAllCountsAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-analyze-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      // Handler file
      await File.WriteAllTextAsync(Path.Combine(tempDir, "Handler.cs"), """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """);

      // Projection file
      await File.WriteAllTextAsync(Path.Combine(tempDir, "Projection.cs"), """
        using Marten.Events.Aggregation;

        public class OrderProjection : SingleStreamProjection<Order> {
          public void Apply(OrderCreated @event, Order state) { }
        }

        public class Order { }
        public record OrderCreated(string Id);
        """);

      var command = new AnalyzeCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.WolverineHandlerCount).IsEqualTo(1);
      await Assert.That(result.MartenProjectionCount).IsEqualTo(1);
      await Assert.That(result.TotalMigrationItems).IsEqualTo(2);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_NestedDirectories_FindsAllFilesAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-analyze-{Guid.NewGuid():N}");
    var nestedDir = Path.Combine(tempDir, "Handlers", "Orders");
    Directory.CreateDirectory(nestedDir);

    try {
      await File.WriteAllTextAsync(Path.Combine(nestedDir, "Handler.cs"), """
        using Wolverine;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """);

      var command = new AnalyzeCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.WolverineHandlerCount).IsEqualTo(1);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }
}
