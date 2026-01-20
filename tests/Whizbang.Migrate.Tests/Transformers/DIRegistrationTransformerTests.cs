using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the DI Registration transformer that converts Wolverine/Marten DI setup to Whizbang.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/DIRegistrationTransformer.cs:*</tests>
public class DIRegistrationTransformerTests {
  [Test]
  public async Task TransformAsync_ConvertsAddWolverineToAddWhizbang_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("AddWhizbang()");
    await Assert.That(result.TransformedCode).DoesNotContain("AddWolverine()");
  }

  [Test]
  public async Task TransformAsync_ConvertsAddWolverineWithOptions_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine(opts => {
        opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
      });
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("AddWhizbang(");
    await Assert.That(result.TransformedCode).DoesNotContain("AddWolverine(");
  }

  [Test]
  public async Task TransformAsync_ConvertsAddMartenToAddWhizbangEventStore_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Marten;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddMarten(connectionString);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("AddWhizbangEventStore(");
    await Assert.That(result.TransformedCode).DoesNotContain("AddMarten(");
  }

  [Test]
  public async Task TransformAsync_ConvertsAddMartenWithOptions_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Marten;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddMarten(opts => {
        opts.Connection(connectionString);
        opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
      });
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("AddWhizbangEventStore(");
    await Assert.That(result.TransformedCode).DoesNotContain("AddMarten(");
  }

  [Test]
  public async Task TransformAsync_ConvertsUseWolverineToUseWhizbang_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;

      var app = builder.Build();
      app.UseWolverine();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("UseWhizbang()");
    await Assert.That(result.TransformedCode).DoesNotContain("UseWolverine()");
  }

  [Test]
  public async Task TransformAsync_UpdatesUsingDirectives_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;
      using Marten;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      builder.Services.AddMarten(connectionString);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten;");
  }

  [Test]
  public async Task TransformAsync_TracksChanges_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_NoDIRegistrations_ReturnsUnchanged_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      public class OrderService {
        public void Process() { }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_ConvertsIntegrateWithWolverine_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;
      using Marten;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddMarten(opts => {
        opts.Connection(connectionString);
      }).IntegrateWithWolverine();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("IntegrateWithWolverine()");
  }

  [Test]
  public async Task TransformAsync_PreservesOtherServiceRegistrations_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;
      using Microsoft.Extensions.DependencyInjection;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      builder.Services.AddScoped<IOrderService, OrderService>();
      builder.Services.AddSingleton<ICache, MemoryCache>();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("AddScoped<IOrderService, OrderService>()");
    await Assert.That(result.TransformedCode).Contains("AddSingleton<ICache, MemoryCache>()");
  }

  [Test]
  public async Task TransformAsync_HandlesHostBuilderPattern_Async() {
    // Arrange
    var transformer = new DIRegistrationTransformer();
    var sourceCode = """
      using Wolverine;

      Host.CreateDefaultBuilder(args)
        .UseWolverine()
        .Build();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("UseWhizbang()");
    await Assert.That(result.TransformedCode).DoesNotContain("UseWolverine()");
  }
}
