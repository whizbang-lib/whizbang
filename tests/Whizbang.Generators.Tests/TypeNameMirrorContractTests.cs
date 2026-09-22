using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The mirror contract between the two type-naming helpers (issues #697, #698): what the generator
/// writes with <see cref="TypeNameUtilities"/> at compile time must be exactly what the runtime
/// produces with <see cref="TypeNameFormatter"/> for the same type, because those strings are keys
/// (<c>clr_type_name</c>, <c>event_type</c>, registry JSON) written by one side and looked up by the
/// other. One corpus is compiled once; the Roslyn symbols and the loaded CLR types come from the
/// same source, so every shape is compared on the same declaration: top level, nested, doubly
/// nested, generic, generic nested in generic, global namespace.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
public class TypeNameMirrorContractTests {
  private const string CORPUS = """
    namespace Corpus.Deep {
      public class TopLevel { }
      public static class Outer {
        public class Inner { }
        public static class Middle {
          public class Innermost { }
        }
      }
      public class Generic<T> { }
      public class GenericOuter<T> {
        public class GenericInner<U> { }
      }
    }
    public class GlobalType { }
    """;

  private static readonly string[] _corpusMetadataNames = [
    "Corpus.Deep.TopLevel",
    "Corpus.Deep.Outer+Inner",
    "Corpus.Deep.Outer+Middle+Innermost",
    "Corpus.Deep.Generic`1",
    "Corpus.Deep.GenericOuter`1+GenericInner`1",
    "GlobalType",
  ];

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ClrTypeName_GeneratorAndRuntimeHelpers_AgreeOnEveryShapeAsync() {
    var (compilation, assembly) = _compileAndLoad();

    foreach (var metadataName in _corpusMetadataNames) {
      var symbol = compilation.GetTypeByMetadataName(metadataName);
      await Assert.That(symbol).IsNotNull().Because($"{metadataName} is in the corpus");
      var type = assembly.GetType(metadataName, throwOnError: true)!;

      var generatorSide = TypeNameUtilities.BuildClrTypeName(symbol!);
      var runtimeSide = TypeNameFormatter.FormatClrTypeName(type);

      await Assert.That(generatorSide).IsEqualTo(runtimeSide)
        .Because($"the registry key written at compile time must be the key the runtime looks up ({metadataName})");
      await Assert.That(assembly.GetType(generatorSide)).IsNotNull()
        .Because("the CLR form resolves back to the type by name");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WireName_GeneratorAndRuntimeHelpers_AgreeOnEveryShapeAsync() {
    var (compilation, assembly) = _compileAndLoad();

    foreach (var metadataName in _corpusMetadataNames) {
      var symbol = compilation.GetTypeByMetadataName(metadataName)!;
      var type = assembly.GetType(metadataName, throwOnError: true)!;

      await Assert.That(TypeNameUtilities.FormatTypeNameForRuntime(symbol)).IsEqualTo(TypeNameFormatter.Format(type))
        .Because($"event_type is written in the wire form on both sides ({metadataName})");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WireName_JaggedArrayOfACorpusType_AgreesAndUsesTheInnermostElementsAssemblyAsync() {
    // Issue #706: the generator unwrapped exactly one array level to find the assembly (array
    // symbols have no ContainingAssembly), so a jagged array's element, itself an array, threw.
    // The throw escaped the perspective discovery generator and every registration in the
    // assembly was lost instead of the one event type being reported.
    var (compilation, assembly) = _compileAndLoad();
    var element = compilation.GetTypeByMetadataName("Corpus.Deep.TopLevel")!;
    var jagged = compilation.CreateArrayTypeSymbol(compilation.CreateArrayTypeSymbol(element));
    var type = assembly.GetType("Corpus.Deep.TopLevel", throwOnError: true)!.MakeArrayType().MakeArrayType();

    await Assert.That(TypeNameUtilities.FormatTypeNameForRuntime(jagged)).IsEqualTo(TypeNameFormatter.Format(type))
      .Because("every array level unwraps to the innermost element, whose assembly is the array's assembly");
  }

  private static (CSharpCompilation Compilation, System.Reflection.Assembly Assembly) _compileAndLoad() {
    var compilation = GeneratorTestHelper.CreateCompilation(CORPUS, assemblyName: "TypeNameMirrorCorpus");
    using var stream = new MemoryStream();
    var emit = compilation.Emit(stream);
    if (!emit.Success) {
      throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics.Select(d => d.ToString())));
    }
    return (compilation, System.Reflection.Assembly.Load(stream.ToArray()));
  }
}
