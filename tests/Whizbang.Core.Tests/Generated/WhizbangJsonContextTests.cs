using System.Text.Json;
using TUnit.Assertions;
using Whizbang.Core.Generated;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for WhizbangJsonContext - AOT-compatible JSON context registration.
/// </summary>
[Category("Serialization")]
public class WhizbangJsonContextTests {

  [Test]
  public async Task Initialize_RegistersContextsWithRegistry_Async() {
    // Arrange & Act
    // Initialize() has already run via [ModuleInitializer]
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert
    // Verify that contexts are registered by checking TypeInfoResolverChain
    await Assert.That(options).IsNotNull();
    await Assert.That(options.TypeInfoResolver).IsNotNull();

    // TypeInfoResolverChain contains all registered contexts
    // Minimum expected: WhizbangIdJsonContext (from Core) + WhizbangIdJsonContext (local) + MessageJsonContext
    await Assert.That(options.TypeInfoResolverChain).IsNotNull();
    await Assert.That(options.TypeInfoResolverChain).HasCount().GreaterThanOrEqualTo(2);

    // Verify JsonContextRegistry has contexts registered
    await Assert.That(JsonContextRegistry.RegisteredCount).IsGreaterThan(0);
  }

  [Test]
  public async Task Initialize_RegistersConverters_Async() {
    // Arrange & Act
    // Initialize() has already run via [ModuleInitializer]
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert
    // Verify converters collection is populated
    await Assert.That(options.Converters).IsNotNull();
    await Assert.That(options.Converters).IsNotEmpty();

    // Verify specific converter types are registered
    // MessageJsonContext.Initialize() registers MessageIdJsonConverter and CorrelationIdJsonConverter
    var converterTypeNames = options.Converters.Select(c => c.GetType().Name).ToList();

    // Check for MessageId and CorrelationId converters
    var hasMessageIdConverter = converterTypeNames.Any(name => name.Contains("MessageId"));
    var hasCorrelationIdConverter = converterTypeNames.Any(name => name.Contains("CorrelationId"));

    await Assert.That(hasMessageIdConverter).IsTrue();
    await Assert.That(hasCorrelationIdConverter).IsTrue();
  }

  [Test]
  public async Task Initialize_RunsBeforeMain_ViaModuleInitializerAsync() {
    // Arrange & Act
    // Initialize() has already run via [ModuleInitializer] before test execution
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert
    // Verify that the registry is not empty, which proves the module initializer ran
    await Assert.That(options).IsNotNull();
    await Assert.That(options.TypeInfoResolverChain).IsNotNull();
    await Assert.That(options.TypeInfoResolverChain).HasCount().GreaterThan(0);

    // Verify JsonContextRegistry has contexts registered (proves module initializer executed)
    await Assert.That(JsonContextRegistry.RegisteredCount).IsGreaterThan(0);

    // Verify converters were also registered (additional validation)
    await Assert.That(options.Converters).IsNotEmpty();
  }
}
