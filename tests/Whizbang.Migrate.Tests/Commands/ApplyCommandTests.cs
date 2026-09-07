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

  [Test]
  public async Task ExecuteAsync_HandlersAndProjectionsSkippedWithJsonMigrationOff_LeavesFileUntouchedAsync() {
    // If the "every category was declined" short-circuit stops firing, a developer who opts out
    // of handlers, projections, and JSON dead-import removal still gets their file rewritten --
    // e.g. the handler conversion or the always-on dead-import removal runs anyway, so "skip
    // everything" silently stops meaning "leave my source alone."
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "Handler.cs");
      const string originalContent = """
        using Wolverine;
        using Newtonsoft.Json;

        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public record CreateOrderCommand(string OrderId);
        """;
      await File.WriteAllTextAsync(sourceFile, originalContent);

      var decisions = DecisionFile.Create(tempDir);
      decisions.Decisions.Handlers.Default = DecisionChoice.Skip;
      decisions.Decisions.Projections.Default = DecisionChoice.Skip;
      decisions.Decisions.JsonMigration.RemoveDeadImports = false;

      var result = await new ApplyCommand().ExecuteAsync(tempDir, decisionFile: decisions);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.SkippedFileCount).IsEqualTo(1)
        .Because("the only file in the project declined every category and must count as skipped");
      await Assert.That(result.TransformedFileCount).IsEqualTo(0)
        .Because("a skipped file must not also be reported as transformed");

      var currentContent = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(currentContent).IsEqualTo(originalContent)
        .Because("declining handlers, projections, and JSON dead-import removal must leave the file byte-for-byte as written");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_GlobalUsingAliasTargetsMartenType_RewritesAliasInPlaceAsync() {
    // If the pipeline computes the global-using-alias transformer's rewrite but discards it, a
    // project-wide alias such as "global using X = Marten.IDocumentStore;" keeps compiling
    // against a package the migration is meant to remove, and nothing downstream ever sees the
    // Whizbang equivalent it should have been pointed at.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "GlobalAlias.cs");
      await File.WriteAllTextAsync(sourceFile, """
        global using MartenStoreAlias = Marten.IDocumentStore;

        public class PlaceholderService {
        }
        """);

      var result = await new ApplyCommand().ExecuteAsync(tempDir);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsEqualTo(1)
        .Because("the alias transformer's output must count as a real file transformation");

      var transformed = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformed).Contains("global using MartenStoreAlias = Whizbang.Core.Messaging.IEventStore;")
        .Because("the alias must be rewritten to the Whizbang equivalent on disk, not just reported in the result");
      await Assert.That(transformed).DoesNotContain("Marten.IDocumentStore")
        .Because("the original Marten target must not survive the rewrite");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_MarkerInterfaceWithWolverineUsingAndNoOtherPatterns_SwapsUsingToWhizbangCoreAsync() {
    // If the marker-interface transformer's rewrite is computed but never applied to the file
    // handed to the next stage, a record implementing IEvent keeps "using Wolverine;" and stops
    // compiling the moment the Wolverine package is removed from the migrated project.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "MarkerOnly.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Wolverine;

        public record OrderPlacedEvent(string OrderId) : IEvent;
        """);

      var result = await new ApplyCommand().ExecuteAsync(tempDir);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsEqualTo(1)
        .Because("the marker-interface rewrite must count as a real file transformation");

      var transformed = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformed).Contains("using Whizbang.Core;")
        .Because("the Wolverine import must be swapped to Whizbang.Core on disk");
      await Assert.That(transformed).DoesNotContain("using Wolverine;")
        .Because("the old import must not survive alongside the new one");
      await Assert.That(transformed).Contains(": IEvent")
        .Because("the marker interface name itself is untouched -- only its import moves");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_UnusedNewtonsoftImportWithNoDecisionFile_RemovesDeadImportAsync() {
    // If the JSON transformer's dead-import removal is computed but never written back to the
    // file, every migrated project keeps a reference to Newtonsoft.Json it no longer needs -- an
    // unused import that compiles fine today but breaks the build the moment the package
    // reference is dropped, which is exactly what a "completed" migration implies happened.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-apply-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var sourceFile = Path.Combine(tempDir, "UnusedImport.cs");
      await File.WriteAllTextAsync(sourceFile, """
        using Newtonsoft.Json;

        public class SampleService {
          public void DoWork() {
          }
        }
        """);

      var result = await new ApplyCommand().ExecuteAsync(tempDir);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.TransformedFileCount).IsEqualTo(1)
        .Because("removing a dead import must still count as a file transformation");

      var transformed = await File.ReadAllTextAsync(sourceFile);
      await Assert.That(transformed).DoesNotContain("Newtonsoft")
        .Because("the unused import must actually be removed from the file on disk, not just reported in the result");
      await Assert.That(transformed).Contains("public class SampleService")
        .Because("removing the dead import must not disturb the rest of the file");
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
