using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// How the JSON context generator walks a message's collection and dictionary property types to
/// find the element types it must register.
/// </summary>
/// <remarks>
/// The registration is what makes a payload round-trip under source-generated serialization with
/// no reflection to fall back on. An element type the walk misses is not a build error — the
/// generated context simply has no entry for it, and the failure arrives at runtime the first
/// time a message carries a populated collection of that type.
///
/// <para>
/// Dictionaries are where the walk gets hard: it wants the VALUE type, which means splitting on
/// the comma that separates the two type arguments — and that comma has to be the top-level one.
/// A nested generic value like <c>Dictionary&lt;string, List&lt;Line&gt;&gt;</c> contains no
/// comma inside its own brackets, but <c>Dictionary&lt;string, Dictionary&lt;string, Line&gt;&gt;</c>
/// does, and splitting on the first comma found would register a fragment of a type name.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class MessageJsonContextNestedGenericTests {

  private static string _contextFor(string properties, string extraTypes = "") {
    var source = $$"""
      using System.Collections.Generic;
      using Whizbang.Core;

      namespace MyApp.Events;

      {{extraTypes}}

      public record ProbeEvent : IEvent {
      {{properties}}
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    return string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
  }

  private const string LINE_TYPE = """
    public record Line {
      public string Sku { get; init; } = "";
      public int Quantity { get; init; }
    }
    """;

  // ============================================================
  // Collections
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AListOfAComplexType_RegistersTheElementAsync() {
    var generated = _contextFor("""
      public List<Line> Lines { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnIReadOnlyListOfAComplexType_RegistersTheElementAsync() {
    // The interface forms are what a well-behaved contract actually exposes.
    var generated = _contextFor("""
      public IReadOnlyList<Line> Lines { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line");
  }

  // ============================================================
  // Dictionaries — the value type, found past the top-level comma
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADictionaryWithAComplexValue_RegistersTheValueTypeAsync() {
    var generated = _contextFor("""
      public Dictionary<string, Line> BySku { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line")
      .Because("the value type is the one that needs a registration — the key is a string");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADictionaryWhoseValueIsItselfGeneric_IsSplitAtTheTopLevelCommaAsync() {
    // List<Line> carries its own brackets. A split that ignored nesting would still land on the
    // right comma here, so this is the easier half — but it must not regress.
    var generated = _contextFor("""
      public Dictionary<string, List<Line>> BySku { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADictionaryOfDictionaries_DoesNotSplitOnTheInnerCommaAsync() {
    // The case the depth counter exists for: the value type contains a comma of its own.
    // Splitting on the first comma found would take "string" as the value type and register a
    // fragment of the real one.
    var generated = _contextFor("""
      public Dictionary<string, Dictionary<string, Line>> ByTenantAndSku { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line")
      .Because("splitting on the inner comma would register a fragment and leave Line unknown "
             + "until the first message carries one");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnIReadOnlyDictionaryIsWalkedTooAsync() {
    var generated = _contextFor("""
      public IReadOnlyDictionary<string, Line> BySku { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line");
  }

  // ============================================================
  // Shapes the walk must simply survive
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ANonGenericProperty_IsHandledAsync() {
    // No brackets and no comma at all — the scan has to run off the end and say "nothing here"
    // rather than indexing past it.
    var generated = _contextFor("""
      public string Name { get; init; } = "";
    """);

    await Assert.That(generated).Contains("ProbeEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADeeplyNestedValueTypeIsStillFoundAsync() {
    var generated = _contextFor("""
      public Dictionary<string, List<Dictionary<string, Line>>> Deep { get; init; } = [];
    """, LINE_TYPE);

    await Assert.That(generated).Contains("Line");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryShapeTogetherStillGeneratesWithoutErrorsAsync() {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>($$"""
      using System.Collections.Generic;
      using Whizbang.Core;

      namespace MyApp.Events;

      {{LINE_TYPE}}

      public record ProbeEvent : IEvent {
        public string Name { get; init; } = "";
        public List<Line> Lines { get; init; } = [];
        public IReadOnlyList<Line> ReadOnlyLines { get; init; } = [];
        public Dictionary<string, Line> BySku { get; init; } = [];
        public Dictionary<string, List<Line>> ListsBySku { get; init; } = [];
        public Dictionary<string, Dictionary<string, Line>> ByTenantAndSku { get; init; } = [];
      }
      """);

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
  }
}
