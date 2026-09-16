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
  /// Test that a [WhizbangId] struct produces an object-mode type info: written as
  /// {"Value":"guid"}, read from that shape or from the scalar string an older row may hold.
  /// </summary>
  /// <remarks>
  /// A document stored as one value once took its identifiers in the scalar form when the atomic
  /// path was unavailable and Entity Framework wrote it under the default profile. A converter can
  /// read both; object metadata could read only the object. The scalar read is counted, so the
  /// tolerance can be removed once nothing needs it.
  /// </remarks>
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

    // A converter, not object metadata: the object form on write, either form on read
    await Assert.That(generated).Contains("private sealed class _ProductIdDocumentConverter : JsonConverter<global::MyApp.Domain.ProductId>");
    await Assert.That(generated).Contains("return JsonMetadataServices.CreateValueInfo<global::MyApp.Domain.ProductId>(options, new _ProductIdDocumentConverter());");
    await Assert.That(generated).Contains("writer.WriteString(\"Value\", value.Value);")
      .Because("the object form is what Entity Framework writes for the same property, and the two writers must agree");
    await Assert.That(generated).Contains("if (reader.TokenType == JsonTokenType.String) {");
    await Assert.That(generated).Contains("global::Whizbang.Core.Perspectives.StoredFormFallbacks.ScalarIdentifierRead(\"ProductId\");")
      .Because("a scalar read is a row the atomic path did not write, and the count decides when the tolerance goes");
    await Assert.That(generated).Contains("return new global::MyApp.Domain.ProductId(reader.GetGuid());");
    await Assert.That(generated).DoesNotContain("JsonObjectInfoValues<global::MyApp.Domain.ProductId>")
      .Because("object metadata reads the object form and nothing else");
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
    await Assert.That(generated).Contains(
      "new JsonSerializerOptions(global::Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions("
      + "global::Whizbang.Core.Serialization.SerializationProfile.Persistence))")
      .Because("the factory's options are persistence options in every respect, the profile's "
        + "converters included, or a document serialized with them directly takes the wire's form");
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

  /// <summary>
  /// A perspective still open in its model type is skipped, because there is no model to read.
  /// </summary>
  /// <remarks>
  /// A generic base such as <c>class Base&lt;T&gt; : IPerspectiveFor&lt;T, …&gt;</c> is a reasonable
  /// thing to write, and its type argument is a type parameter rather than a type. Nothing can be
  /// discovered from it: the temporal properties of <c>T</c> are whatever the closing type decides,
  /// and that type is where the discovery belongs. Skipping is what makes the open base harmless
  /// rather than a build failure or, worse, a converter registered for a type parameter.
  /// </remarks>
  [Test]
  public async Task Generator_WithAnOpenPerspective_IsSkippedAsync() {
    const string source = """
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record OpenCreated : IEvent;

        public class OpenBasePerspective<TModel>
          : IPerspectiveFor<TModel, OpenCreated> {
          public TModel Apply(TModel currentData, OpenCreated @event) => currentData;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNull()
      .Because("the model is a type parameter, so there is nothing to discover and nothing to register");
  }

  /// <summary>
  /// A perspective whose model holds dates is wired like any other, and nothing about its dates is
  /// named in the generated code.
  /// </summary>
  /// <remarks>
  /// The writer half of the stored format used to be emitted here as a per-model list of temporal
  /// property names, ordered so the output was stable. The list was the partial discovery that
  /// missed inherited, nested and collection-element temporals, and it is gone: the persistence
  /// profile's own converters reach every temporal in every document. This pins that the generator
  /// names no model and no property for it, so the list cannot quietly come back.
  /// </remarks>
  [Test]
  public async Task Generator_WithTemporalModels_NamesNothingPerModelAsync() {
    const string source = """
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record ZebraDto(DateTime OccurredAt);

        public record AlpacaDto(DateTime OccurredAt);

        public record TemporalCreated : IEvent;

        public class ZebraPerspective : IPerspectiveFor<ZebraDto, TemporalCreated> {
          public ZebraDto Apply(ZebraDto currentData, TemporalCreated @event) => currentData;
        }

        public class AlpacaPerspective : IPerspectiveFor<AlpacaDto, TemporalCreated> {
          public AlpacaDto Apply(AlpacaDto currentData, TemporalCreated @event) => currentData;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectivePersistenceJsonContextGenerator>(source);

    var callback = GeneratorTestHelper.GetGeneratedSource(result, "PerspectivePersistenceCallbackInitializer.g.cs");
    await Assert.That(callback).IsNotNull()
      .Because("a perspective is a perspective; its atomic-upsert options are wired whatever it holds");

    await Assert.That(callback).DoesNotContain("OccurredAt", StringComparison.Ordinal)
      .Because("a property named here is a list the reader no longer shares");
    await Assert.That(callback).DoesNotContain("AlpacaDto", StringComparison.Ordinal);
    await Assert.That(callback).DoesNotContain("ZebraDto", StringComparison.Ordinal);
    await Assert.That(callback).DoesNotContain("RegisterTypeInfoModifier", StringComparison.Ordinal);
  }

}
