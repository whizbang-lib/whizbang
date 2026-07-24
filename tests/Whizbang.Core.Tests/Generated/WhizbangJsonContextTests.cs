using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    await Assert.That(options.TypeInfoResolverChain).Count().IsGreaterThanOrEqualTo(2);

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
    await Assert.That(options.TypeInfoResolverChain).Count().IsGreaterThan(0);

    // Verify JsonContextRegistry has contexts registered (proves module initializer executed)
    await Assert.That(JsonContextRegistry.RegisteredCount).IsGreaterThan(0);

    // Verify converters were also registered (additional validation)
    await Assert.That(options.Converters).IsNotEmpty();
  }

  [Test]
  public async Task Initialize_RegistersLenientDateTimeOffsetConverters_Async() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var names = options.Converters.Select(c => c.GetType().Name).ToList();

    await Assert.That(names.Any(n => n == nameof(LenientDateTimeOffsetConverter))).IsTrue();
    await Assert.That(names.Any(n => n == nameof(LenientNullableDateTimeOffsetConverter))).IsTrue();
  }

  [Test]
  public async Task CreateCombinedOptions_DeserializesPostgresInfinityTimestamp_Async() {
    // CreateCombinedOptions() only knows source-generated types, so it cannot resolve
    // a test-local record by itself. Append a reflection fallback resolver for the
    // test model — the globally-registered Lenient converters still win for
    // DateTimeOffset properties because options.Converters is consulted before
    // the type info resolver's default converter. This mirrors the a consumer application failure
    // path: type info comes from a consumer application's source-gen context, but the DateTimeOffset
    // converter must come from the global registry.
    var options = JsonContextRegistry.CreateCombinedOptions();
    options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    var json = "{\"CreatedAt\":\"-infinity\",\"DeletedAt\":\"infinity\"}";

    var model = JsonSerializer.Deserialize<_InfinityTestModel>(json, options);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.CreatedAt).IsEqualTo(DateTimeOffset.MinValue);
    await Assert.That(model.DeletedAt).IsEqualTo(DateTimeOffset.MaxValue);
  }

  private sealed record _InfinityTestModel(DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt);
}
