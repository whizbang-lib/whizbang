using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Wizard;

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
      const string originalContent = """
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

  // ── The full transformer pipeline over one file ───────────────────────────

  [Test]
  public async Task ExecuteAsync_RunsEveryTransformerOverASingleFileAsync() {
    // apply chains a dozen transformers over each file, accumulating changes from each. Every
    // existing fixture triggers one or two of them, so most of that chain has never run in a
    // test. If it short-circuited after the first transformer that matched -- an easy thing to
    // introduce while refactoring the loop -- the later ones would silently stop running and a
    // migration would report success having done a fraction of the work.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-pipeline-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Everything.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using System;
        using Wolverine;
        using Wolverine.Http;
        using Marten;
        using HotChocolate.Data.Marten;

        public record OrderPlaced(Guid Id) : IEvent;

        public class OrderEndpoints {
          [WolverineGet("/orders")]
          public string List() => "orders";
        }

        public class OrderService {
          private readonly IDocumentSession _session;
          public OrderService(IDocumentSession session) { _session = session; }

          public Guid NewOrderId() => Guid.NewGuid();
        }

        public static class Startup {
          public static void Configure(IServiceCollection services, IRequestExecutorBuilder builder) {
            services.AddMarten(o => { });
            builder.AddMartenFiltering();
          }
        }
        """);

      var result = await new ApplyCommand().ExecuteAsync(tempDir);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsGreaterThan(0);

      var transformed = await File.ReadAllTextAsync(sourceFile);

      // Each assertion below is a different transformer's fingerprint. Collectively they show
      // the chain ran past its first match rather than stopping there.
      await Assert.That(transformed).Contains("idProvider.NewGuid()")
        .Because("the id transformer routes Guid.NewGuid() through the injected provider");
      await Assert.That(_countOccurrences(transformed, "using Whizbang.Core;")).IsEqualTo(1)
        .Because("a file importing both Wolverine and Marten has two usings rewritten to "
               + "Whizbang.Core; emitting both raises CS0105 and fails a warnings-as-errors build");
      await Assert.That(transformed).Contains("AddWhizbangLenses")
        .Because("the HotChocolate transformer replaces AddMartenFiltering");
      await Assert.That(transformed).DoesNotContain("WolverineGet")
        .Because("the Wolverine HTTP transformer strips routing attributes that no longer compile");
      await Assert.That(transformed).Contains("TODO")
        .Because("that stripped route has to leave a marker behind, or the endpoint vanishes silently");

      await Assert.That(result.Changes[0].Changes.Count).IsGreaterThan(2)
        .Because("changes from several transformers accumulate for one file, they do not replace each other");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_DecisionFileSkippingGuids_LeavesGuidCallsAloneAsync() {
    // The Guid rewrite is the one transformer gated on a decision. A consumer who wants their
    // own id strategy turns it off, and it must then leave Guid.NewGuid() intact while the rest
    // of the pipeline still runs -- the setting scopes one transformer, it does not halt apply.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-pipeline-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Ids.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using System;
        using Wolverine;
        using HotChocolate.Data.Marten;

        public class OrderService {
          public Guid NewOrderId() => Guid.NewGuid();
        }

        public static class Startup {
          public static void Configure(IRequestExecutorBuilder builder) {
            builder.AddMartenFiltering();
          }
        }
        """);

      var decisions = DecisionFile.Create(tempDir);
      decisions.Decisions.IdGeneration.GuidNewGuid = DecisionChoice.Skip;

      var result = await new ApplyCommand().ExecuteAsync(tempDir, decisionFile: decisions);

      await Assert.That(result.Success).IsTrue();

      var transformed = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformed).Contains("Guid.NewGuid()")
        .Because("the operator opted out of the id rewrite, so their call has to survive");
      await Assert.That(transformed).Contains("AddWhizbangLenses")
        .Because("skipping one transformer must not stop the others from running");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }


  private static int _countOccurrences(string haystack, string needle) {
    var count = 0;
    var index = haystack.IndexOf(needle, StringComparison.Ordinal);

    while (index >= 0) {
      count++;
      index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
    }

    return count;
  }

}
