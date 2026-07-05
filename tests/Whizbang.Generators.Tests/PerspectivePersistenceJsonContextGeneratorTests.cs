using System.Diagnostics.CodeAnalysis;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="PerspectivePersistenceJsonContextGenerator"/>.
/// Verifies discovery of [WhizbangId] structs, the emitted object-mode
/// PerspectivePersistenceJsonContext resolver, and the ModuleInitializer callback
/// initializer that auto-wires the Path 1 atomic-upsert options provider when
/// perspectives are present in the consuming assembly.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/PerspectivePersistenceJsonContextGenerator.cs</tests>
[Category("SourceGenerators")]
public class PerspectivePersistenceJsonContextGeneratorTests {
  /// <summary>
  /// Source with a single [WhizbangId] partial struct and no perspectives.
  /// </summary>
  private const string SINGLE_ID_SOURCE = """
      using Whizbang.Core;

      namespace MyApp.Domain;

      [WhizbangId]
      public readonly partial struct ProductId;
      """;

  /// <summary>
  /// Source with a class-based perspective (real Whizbang.Core interfaces).
  /// </summary>
  private const string CLASS_PERSPECTIVE_SOURCE = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      """;

  /// <summary>
  /// Test that a [WhizbangId] struct produces an object-mode JsonTypeInfo factory
  /// with the {"Value":"guid"} shape (parameterized constructor + Value property, no setter).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangIdStruct_EmitsObjectModeTypeInfoAsync() {
    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(SINGLE_ID_SOURCE);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();

    // Resolver class + dispatch to the per-type factory
    await Assert.That(generated).Contains("public sealed class PerspectivePersistenceJsonContext : IJsonTypeInfoResolver");
    await Assert.That(generated).Contains("if (type == typeof(global::MyApp.Domain.ProductId)) {");
    await Assert.That(generated).Contains("return _createProductIdTypeInfo(options);");

    // Object-mode metadata: parameterized constructor over a Guid "Value" parameter
    await Assert.That(generated).Contains("JsonObjectInfoValues<global::MyApp.Domain.ProductId>");
    await Assert.That(generated).Contains("ObjectWithParameterizedConstructorCreator = static args => new global::MyApp.Domain.ProductId((global::System.Guid)args[0]!)");
    await Assert.That(generated).Contains("ConstructorParameterMetadataInitializer");
    await Assert.That(generated).Contains("ParameterType = typeof(global::System.Guid)");

    // Property metadata: read-only "Value" property (Setter = null, getter reads .Value)
    await Assert.That(generated).Contains("Getter = static obj => ((global::MyApp.Domain.ProductId)obj!).Value");
    await Assert.That(generated).Contains("Setter = null");
    await Assert.That(generated).Contains("JsonPropertyName = \"Value\"");
  }

  /// <summary>
  /// Test that a nullable counterpart factory is emitted for each [WhizbangId] struct.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangIdStruct_EmitsNullableTypeInfoAsync() {
    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(SINGLE_ID_SOURCE);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains("if (type == typeof(global::MyApp.Domain.ProductId?)) {");
    await Assert.That(generated).Contains("return _createProductIdNullableTypeInfo(options);");
    await Assert.That(generated).Contains("JsonMetadataServices.GetNullableConverter<global::MyApp.Domain.ProductId>(options)");
    await Assert.That(generated).Contains("JsonMetadataServices.CreateValueInfo<global::MyApp.Domain.ProductId?>(options, converter)");
  }

  /// <summary>
  /// Test that the CreateOptions factory wires the resolver chain with this context first
  /// and applies WhenWritingNull, and that the context lands in the assembly's Generated namespace.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EmitsCreateOptionsFactoryInAssemblyNamespaceAsync() {
    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(SINGLE_ID_SOURCE);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();

    // Test compilation is named "TestAssembly" - namespace derives from assembly name
    await Assert.That(generated).Contains("namespace TestAssembly.Generated;");
    await Assert.That(generated).Contains("public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] additionalResolvers)");
    await Assert.That(generated).Contains("resolvers[0] = Default;");
    await Assert.That(generated).Contains("TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers)");
    await Assert.That(generated).Contains("DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull");
  }

  /// <summary>
  /// Test that source with no [WhizbangId] structs and no perspectives still produces
  /// the resolver (returning null for every type) but NO callback initializer.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutWhizbangIdsOrPerspectives_EmitsEmptyResolverAndNoCallbackAsync() {
    // Arrange
    const string source = """
        namespace TestApp;

        public class PlainClass {
          public string Name { get; set; } = "";
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert - resolver is emitted with an empty body
    var context = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(context).IsNotNull();
    await Assert.That(context).Contains("No [WhizbangId] types discovered in this assembly.");
    await Assert.That(context).Contains("return null;");

    // No perspectives => no ModuleInitializer callback file
    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNull();
  }

  /// <summary>
  /// Test that a non-partial [WhizbangId] struct is skipped (matches WhizbangIdGenerator's gate).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NonPartialWhizbangIdStruct_IsSkippedAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;

        namespace MyApp.Domain;

        [WhizbangId]
        public readonly struct ProductId {
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("No [WhizbangId] types discovered in this assembly.");
    await Assert.That(generated).DoesNotContain("ProductId");
  }

  /// <summary>
  /// Test that a partial struct with an unrelated attribute is not treated as a WhizbangId.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_StructWithUnrelatedAttribute_IsSkippedAsync() {
    // Arrange
    const string source = """
        namespace MyApp.Domain;

        [System.Obsolete("not an id")]
        public readonly partial struct NotAnId;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("No [WhizbangId] types discovered in this assembly.");
    await Assert.That(generated).DoesNotContain("NotAnId");
  }

  /// <summary>
  /// Test that multiple [WhizbangId] structs are all emitted, ordered by fully-qualified name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleWhizbangIds_EmitsAllOrderedByFullyQualifiedNameAsync() {
    // Arrange - declared in reverse alphabetical order to prove sorting is by FQN
    const string source = """
        using Whizbang.Core;

        namespace MyApp.Domain;

        [WhizbangId]
        public readonly partial struct ProductId;

        [WhizbangId]
        public readonly partial struct OrderId;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains("if (type == typeof(global::MyApp.Domain.OrderId)) {");
    await Assert.That(generated).Contains("if (type == typeof(global::MyApp.Domain.ProductId)) {");
    await Assert.That(generated).Contains("_createOrderIdTypeInfo");
    await Assert.That(generated).Contains("_createProductIdTypeInfo");

    // Ordinal ordering: OrderId sorts before ProductId
    var orderIndex = generated!.IndexOf("typeof(global::MyApp.Domain.OrderId)", StringComparison.Ordinal);
    var productIndex = generated.IndexOf("typeof(global::MyApp.Domain.ProductId)", StringComparison.Ordinal);
    await Assert.That(orderIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(orderIndex).IsLessThan(productIndex);
  }

  /// <summary>
  /// Test that duplicate declarations of the same [WhizbangId] struct (multiple partial parts)
  /// are deduplicated to a single factory method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DuplicateWhizbangIdDeclarations_DeduplicatesFactoriesAsync() {
    // Arrange - two partial declarations of the same struct, each carrying the attribute.
    // Both syntax nodes pass the syntactic predicate; the generator must group by FQN.
    const string source = """
        using Whizbang.Core;

        namespace MyApp.Domain;

        [WhizbangId]
        public readonly partial struct ProductId;

        [WhizbangId]
        public readonly partial struct ProductId {
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert - exactly one factory method definition despite two declarations
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceJsonContext.g.cs");
    await Assert.That(generated).IsNotNull();

    const string factorySignature = "private static JsonTypeInfo<global::MyApp.Domain.ProductId> _createProductIdTypeInfo";
    var factoryCount = generated!.Split(factorySignature).Length - 1;
    await Assert.That(factoryCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test that a class-based perspective triggers emission of the ModuleInitializer
  /// callback initializer that registers with JsonContextRegistry and wires the
  /// Path 1 atomic-upsert options provider through ServiceRegistrationCallbacks.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassPerspective_EmitsCallbackInitializerAsync() {
    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(CLASS_PERSPECTIVE_SOURCE);

    // Assert
    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNotNull();

    await Assert.That(callback).Contains("namespace TestAssembly.Generated;");
    await Assert.That(callback).Contains("internal static class PerspectivePersistenceCallbackInitializer");
    await Assert.That(callback).Contains("[ModuleInitializer]");

    // Registers in the cross-assembly serialization union under the Persistence profile
    await Assert.That(callback).Contains("global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(");
    await Assert.That(callback).Contains("priority: 1000,");
    await Assert.That(callback).Contains("profile: global::Whizbang.Core.Serialization.SerializationProfile.Persistence);");

    // Wires the static hook via ServiceRegistrationCallbacks (order owned by InvokeAll)
    await Assert.That(callback).Contains("ServiceRegistrationCallbacks.PerspectivePersistenceOptions = _ =>");
    await Assert.That(callback).Contains("BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>");
    await Assert.That(callback).Contains("MessageJsonContext.Default,");
    await Assert.That(callback).Contains("global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);");
  }

  /// <summary>
  /// Test that a record-based perspective (RecordDeclarationSyntax discovery branch)
  /// also triggers the callback initializer.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRecordPerspective_EmitsCallbackInitializerAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record ProductDto(string Name);

        public record ProductPerspective(IPerspectiveStore<ProductDto> Store)
          : IPerspectiveFor<ProductDto, ProductCreated> {
          public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNotNull();
    await Assert.That(callback).Contains("[ModuleInitializer]");
  }

  /// <summary>
  /// Test that an abstract perspective base class does NOT count as a perspective
  /// (abstract classes cannot be instantiated) - no callback initializer is emitted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractPerspective_DoesNotEmitCallbackInitializerAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record ProductDto(string Name);

        public abstract class BaseProductPerspective
          : IPerspectiveFor<ProductDto, ProductCreated> {
          public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNull();
  }

  /// <summary>
  /// Test that a class with a base list that is NOT a perspective interface
  /// does not trigger the callback initializer.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonPerspectiveClass_DoesNotEmitCallbackInitializerAsync() {
    // Arrange
    const string source = """
        using System;

        namespace TestApp;

        public class DisposableThing : IDisposable {
          public void Dispose() {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNull();
  }

  /// <summary>
  /// Test that the generated resolver compiles without errors when the [WhizbangId] struct
  /// supplies the Value property and Guid constructor the object-mode metadata references.
  /// No perspectives are present, so only the resolver file is compiled (the callback
  /// initializer requires MessageJsonContext + Whizbang.Data.EFCore.Postgres, which
  /// only exist in a full consumer build).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedContext_WithWhizbangIdAndNoPerspectives_CompilesWithoutErrorsAsync() {
    // Arrange - struct fleshed out manually (WhizbangIdGenerator is not run in this test)
    const string source = """
        using System;
        using Whizbang.Core;

        namespace MyApp.Domain;

        [WhizbangId]
        public readonly partial struct ProductId {
          public Guid Value { get; init; }

          public ProductId(Guid value) {
            Value = value;
          }
        }
        """;

    // Act
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// Test that the empty resolver (no [WhizbangId] types) compiles without errors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedContext_WithNoWhizbangIds_CompilesWithoutErrorsAsync() {
    // Arrange
    const string source = """
        namespace MyApp.Domain;

        public class PlainClass {
          public string Name { get; set; } = "";
        }
        """;

    // Act
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<PerspectivePersistenceJsonContextGenerator>(source);

    // Assert
    await Assert.That(errors).IsEmpty();
  }
}
