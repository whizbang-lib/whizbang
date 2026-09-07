using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for two <see cref="EFCorePerspectiveAssociationGenerator"/> lines
/// <see cref="EFCorePerspectiveAssociationGeneratorTests"/> never happens to exercise: a
/// candidate class that has a base list but implements no perspective interface, and the
/// self-skip guard that keeps the generator from emitting registration code while compiling
/// its OWN library project.
/// </summary>
public class EFCorePerspectiveAssociationGeneratorCoverageTests {

  /// <summary>
  /// Same reference set as <see cref="GeneratorTestHelper"/>'s <c>RunGenerator</c> helper, but
  /// lets the compilation's assembly name be chosen — needed to drive the generator's self-skip
  /// guard, which keys off <c>Compilation.AssemblyName</c>.
  /// </summary>
  [RequiresAssemblyFiles]
  private static GeneratorDriverRunResult _runGenerator(string source, string assemblyName) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new List<MetadataReference>();
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")));

    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch {
      var coreAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Core.dll");
      if (File.Exists(coreAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(coreAssemblyPath));
      }
    }

    var compilation = CSharpCompilation.Create(
        assemblyName: assemblyName,
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var generator = new EFCorePerspectiveAssociationGenerator();
    var driver = CSharpGeneratorDriver.Create(generator);
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
    return driver.GetRunResult();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ClassWithBaseListButNoPerspectiveInterface_IsIgnoredAsync() {
    // The syntax-level filter only narrows to "has a base list" — a class implementing some
    // ordinary interface must still be excluded by the semantic check, not misidentified as a
    // perspective and registered with garbage association data.
    const string source = """
      using System;

      namespace TestNamespace {
        public class NotAPerspective : IDisposable {
          public void Dispose() { }
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNull()
      .Because("a base-list class implementing no perspective interface must not be registered");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CompilingTheLibraryItself_GeneratesNothingAsync() {
    // Even with a real perspective present, the generator must skip emission entirely when it
    // is compiling its OWN library project — otherwise every consumer's build would carry a
    // spurious, empty association-registration method alongside their real one.
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record OrderCreatedEvent : IEvent {
          public string OrderId { get; init; } = "";
        }

        public record OrderModel {
          public string OrderId { get; set; } = "";
        }

        public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
          public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
            return currentData;
          }
        }
      }
      """;

    var result = _runGenerator(source, assemblyName: "Whizbang.Data.EFCore.Postgres");

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNull()
      .Because("the generator must not emit its own registration code while compiling the library "
             + "project itself — the assembly-name guard exists specifically to prevent that");
  }
}
