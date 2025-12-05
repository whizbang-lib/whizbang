using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// TDD tests for EFCorePerspectiveConfigurationGenerator diagnostic output.
/// These tests verify that the generated code implements IWhizbangDiscoveryDiagnostics
/// and provides useful diagnostic information about discovered perspectives.
/// </summary>
public class EFCorePerspectiveConfigurationGeneratorDiagnosticsTests {
  /// <summary>
  /// RED TEST: Generated code should implement IWhizbangDiscoveryDiagnostics interface.
  /// This ensures consumers can use a consistent API across all generators.
  /// </summary>
  [Test]
  public async Task GeneratedCode_ImplementsIDiagnosticsInterfaceAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace Whizbang.Core.Perspectives {
        public interface IPerspectiveStore<TModel> { }
      }

      namespace TestApp;

      public record ProductDto(string Name, decimal Price);

      public class ProductPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductCreated> {

        public Task Update(ProductCreated @event, CancellationToken ct) {
          return Task.CompletedTask;
        }
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated class should implement IWhizbangDiscoveryDiagnostics
    await Assert.That(result.GeneratedSources).HasCount().GreaterThanOrEqualTo(1);

    var generatedCode = result.GeneratedSources
      .FirstOrDefault(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs");

    await Assert.That(generatedCode).IsNotNull();
    await Assert.That(generatedCode!.SourceText.ToString())
      .Contains("IWhizbangDiscoveryDiagnostics");
  }

  /// <summary>
  /// RED TEST: Generated diagnostic class should have correct generator name.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_HasCorrectGeneratorNameAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace Whizbang.Core.Perspectives {
        public interface IPerspectiveStore<TModel> { }
      }

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductCreated> {
        public Task Update(ProductCreated @event, CancellationToken ct) => Task.CompletedTask;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("EFCorePerspectiveConfigurationGenerator");
  }

  /// <summary>
  /// RED TEST: Generated diagnostic should report discovered perspective count.
  /// When 1 perspective is discovered, TotalDiscoveredCount should be 1.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace Whizbang.Core {
        public interface IPerspectiveOf<in TEvent> where TEvent : IEvent {
          Task Update(TEvent @event, CancellationToken cancellationToken = default);
        }
      }

      namespace Whizbang.Core.Perspectives {
        public interface IPerspectiveStore<TModel> { }
      }

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductCreated> {
        public Task Update(ProductCreated @event, CancellationToken ct) => Task.CompletedTask;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 1 perspective
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // Should contain perspective count in the diagnostics output (no deduplication for single perspective)
    await Assert.That(generatedCode).Contains("1 perspective(s)");

    // TotalDiscoveredCount should return 1
    await Assert.That(generatedCode).Contains("TotalDiscoveredCount");
  }

  /// <summary>
  /// RED TEST: LogDiscoveryDiagnostics should output perspective details.
  /// When called, should log each discovered perspective with model type and table name.
  /// </summary>
  [Test]
  public async Task LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace Whizbang.Core {
        public interface IPerspectiveOf<in TEvent> where TEvent : IEvent {
          Task Update(TEvent @event, CancellationToken cancellationToken = default);
        }
      }

      namespace Whizbang.Core.Perspectives {
        public interface IPerspectiveStore<TModel> { }
      }

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductCreated> {
        public Task Update(ProductCreated @event, CancellationToken ct) => Task.CompletedTask;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated LogDiscoveryDiagnostics method should log perspective info
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("LogDiscoveryDiagnostics");
    await Assert.That(generatedCode).Contains("ProductDto");
    await Assert.That(generatedCode).Contains("wh_per_product_dto");  // Whizbang table name with prefix
  }

  /// <summary>
  /// RED TEST: When no perspectives discovered, should report 0 and list fixed entities only.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync() {
    // Arrange - Source with no perspectives
    var source = @"
      using Whizbang.Core;

      namespace TestApp;

      public class SomeClass {
        public void DoSomething() { }
      }
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 0 perspectives
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("0 perspective(s)");
    await Assert.That(generatedCode).Contains("InboxRecord");  // Fixed entities still configured
    await Assert.That(generatedCode).Contains("OutboxRecord");
    await Assert.That(generatedCode).Contains("EventStoreRecord");
  }

  /// <summary>
  /// RED TEST: Multiple perspectives with same model type should be deduplicated.
  /// Only 1 PerspectiveRow<ProductDto> configuration should be generated.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_DeduplicatesPerspectivesAsync() {
    // Arrange - Two perspectives using same model type
    var source = @"
      using Whizbang.Core;

      namespace Whizbang.Core {
        public interface IPerspectiveOf<in TEvent> where TEvent : IEvent {
          Task Update(TEvent @event, CancellationToken cancellationToken = default);
        }
      }

      namespace Whizbang.Core.Perspectives {
        public interface IPerspectiveStore<TModel> { }
      }

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductCreatedPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductCreated> {
        public Task Update(ProductCreated @event, CancellationToken ct) => Task.CompletedTask;
      }

      public class ProductUpdatedPerspective(Whizbang.Core.Perspectives.IPerspectiveStore<ProductDto> store)
        : IPerspectiveOf<ProductUpdated> {
        public Task Update(ProductUpdated @event, CancellationToken ct) => Task.CompletedTask;
      }

      public record ProductCreated : IEvent;
      public record ProductUpdated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 1 unique model type (ProductDto)
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("1 unique model type(s) from 2 perspective(s)");
  }
}
