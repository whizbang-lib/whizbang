using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="MessageTypeCatalogGenerator"/>, complementing
/// <c>MessageTypeCatalogGeneratorTests.cs</c>, <c>MessageTypeCatalogEphemeralTests.cs</c>, and
/// <c>MessageTypeCatalogFingerprintTests.cs</c>. These target the non-public-type exclusion and the
/// three property-shape exclusions inside <c>_serializableProperties</c> that feed the schema-hash
/// fingerprint (F-3).
/// </summary>
/// <remarks>
/// Line 73 (<c>semanticModel.GetDeclaredSymbol(...) is not INamedTypeSymbol =&gt; return null</c>) is
/// not covered here — as established in earlier coverage rounds, <c>GetDeclaredSymbol</c> always
/// returns a real symbol for a <c>ClassDeclarationSyntax</c>/<c>RecordDeclarationSyntax</c>/
/// <c>StructDeclarationSyntax</c> node in a compiling program; this is a defensive Roslyn-contract
/// guard with no reachable path.
/// </remarks>
public class MessageTypeCatalogGeneratorCoverageTests {
  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    return code!;
  }

  // Pulls the hex value of SchemaHash off the generated entry line for a given type.
  private static string _extractSchemaHash(string code, string typeName) {
    var line = code.Split('\n').FirstOrDefault(l => l.Contains($"typeof(global::{typeName})")) ?? "";
    var m = Regex.Match(line, "SchemaHash = \"([0-9a-f]*)\"");
    return m.Success ? m.Groups[1].Value : "";
  }

  // A non-public message type is excluded from the catalog (MessageTypeCatalogGenerator.cs:81): if
  // this regressed, a downstream reader of IMessageTypeCatalog.GetAll() (the type-registry populator,
  // the rename tool) would see an entry for a type it cannot reference or construct outside its
  // declaring assembly.
  [Test]
  public async Task NonPublicMessageType_ExcludedFromCatalogAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record PublicOrderPlaced : IEvent;
      internal record InternalOrderPlaced : IEvent;
""");

    await Assert.That(code).Contains("MyApp.PublicOrderPlaced")
      .Because("the public event must still be cataloged");
    await Assert.That(code).DoesNotContain("InternalOrderPlaced")
      .Because("a non-public type must never appear in the generated (public) catalog");
  }

  // A static property and an indexer are never serialized on the wire, so _serializableProperties
  // skips both (MessageTypeCatalogGenerator.cs:191-192) before computing the schema hash. If this
  // regressed, adding a static helper property or an indexer to an event would silently change its
  // wire-schema fingerprint even though nothing about the actual payload changed, causing the
  // startup reconciler to flag spurious drift against wh_type_definitions.
  [Test]
  public async Task StaticAndIndexerProperties_ExcludedFromSchemaHashAsync() {
    var plain = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record PlainOrder(int OrderId) : IEvent;
"""), "MyApp.PlainOrder");

    var withStaticAndIndexer = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderWithStatic(int OrderId) : IEvent {
        public static string Label => "static-label";
        public int this[int index] => index;
      }
"""), "MyApp.OrderWithStatic");

    await Assert.That(withStaticAndIndexer).IsEqualTo(plain)
      .Because("a static property and an indexer are never part of the serialized payload and must not perturb the schema fingerprint");
  }

  // A property whose getter is not public (System.Text.Json cannot read it during serialization) is
  // excluded (MessageTypeCatalogGenerator.cs:197-198). If this regressed, a property an author
  // deliberately hid from serialization (e.g. an internal-only computed value) would still count
  // toward the wire-schema fingerprint, flagging drift for a change that never touches the wire.
  [Test]
  public async Task PropertyWithNonPublicGetter_ExcludedFromSchemaHashAsync() {
    var baseline = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record BareWidget(int Id) : IEvent;
"""), "MyApp.BareWidget");

    var withPrivateGetter = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record WidgetWithSecret(int Id) : IEvent {
        public string Secret { private get; set; } = "";
      }
"""), "MyApp.WidgetWithSecret");

    await Assert.That(withPrivateGetter).IsEqualTo(baseline)
      .Because("a property with a non-public getter cannot be read by the serializer and must not appear in the wire schema fingerprint");
  }

  // A property literally named "EqualityContract" is excluded by name (MessageTypeCatalogGenerator.cs:200-201) —
  // the same exclusion that makes a genuine record's compiler-generated member a no-op (that one is
  // "protected" and is already filtered by the accessibility check one line above). Using a
  // hand-written, non-record class isolates the by-name exclusion itself: without it, an
  // unrelated public property that merely happens to share that name would leak into the schema hash.
  [Test]
  public async Task PropertyNamedEqualityContract_ExcludedFromSchemaHashAsync() {
    var baseline = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public class BareEvent : IEvent { }
"""), "MyApp.BareEvent");

    var withNamedProperty = _extractSchemaHash(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public class EventWithNamedProperty : IEvent {
        public string EqualityContract => "not-a-record-member";
      }
"""), "MyApp.EventWithNamedProperty");

    await Assert.That(withNamedProperty).IsEqualTo(baseline)
      .Because("a property literally named EqualityContract must be excluded from the schema hash by name, regardless of whether it is the compiler-generated record member");
  }
}
