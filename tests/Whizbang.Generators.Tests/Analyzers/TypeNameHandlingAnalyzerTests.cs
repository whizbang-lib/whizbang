using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// The four type-name handling rules (issue #698): a name composed by hand (WHIZ160), dissected
/// by hand (WHIZ161), compared as a plain string (WHIZ162), or assigned to a key from a hand-built
/// string (WHIZ163). One positive and one negative fixture per rule, plus the exemptions: the
/// shared helpers themselves, prose that happens to contain ", ", and non-string comparisons.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/TypeNameHandlingAnalyzer.cs</code-under-test>
/// <docs>operations/diagnostics/whiz160</docs>
public class TypeNameHandlingAnalyzerTests {
  private const string PRELUDE = """
    using System;
    namespace Whizbang.Core {
      public static class TypeNameFormatter {
        public static string Format(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;
        public static string FormatClrTypeName(Type t) => t.FullName!;
        public static string GetFullName(string typeName) => typeName.Split(',')[0];
      }
    }
    namespace Whizbang.Core.Messaging {
      public static class EventTypeMatchingHelper {
        public static string NormalizeTypeName(string n) => n;
      }
    }
    """;

  private static async Task<List<string>> _idsAsync(string body) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TypeNameHandlingAnalyzer>(PRELUDE + body);
    return diagnostics.Where(d => d.Id.StartsWith("WHIZ16", StringComparison.Ordinal)).Select(d => d.Id).ToList();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Composition_WithTheWireSeparator_ReportsWhiz160Async() {
    const string body = """
      namespace App {
        public class Sample {
          public string Wire(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).Contains("WHIZ160").Because("'FullName, Assembly' is the wire form; the helper renders it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Composition_WithTheNestedSeparator_InAnInterpolation_ReportsWhiz160Async() {
    const string body = """
      namespace App {
        public class Sample {
          public string Clr(Type outer, Type inner) => $"{outer.FullName}+{inner.Name}";
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).Contains("WHIZ160").Because("'+' between a full name and another part is the CLR nested form");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Prose_ThatEndsAClauseWithACommaAfterATypeName_IsNotCompositionAsync() {
    const string body = """
      namespace App {
        public class Sample {
          public string Message(Type t, string detail) => $"No receptor for '{t.FullName}', " + detail + ".";
          public string Sentence(Type t) => "Type " + t.FullName + ", " + "was not found.";
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).IsEmpty().Because("a separator that ends a sentence joins prose, not two parts of a name");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_SplitOnTheAssemblyComma_ReportsWhiz161Async() {
    const string body = """
      namespace App {
        public class Sample {
          public string Bare(string eventType) => eventType.Split(',')[0];
          public string Nested(string clrTypeName) => clrTypeName.Substring(clrTypeName.IndexOf('+') + 1);
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids.Count(id => id == "WHIZ161")).IsGreaterThanOrEqualTo(2)
      .Because("Split on ',' and Substring/IndexOf('+') on a type name are hand parses of the wire and CLR forms");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_ThroughTheHelper_IsCleanAsync() {
    const string body = """
      namespace App {
        public class Sample {
          public string Bare(string eventType) => Whizbang.Core.TypeNameFormatter.GetFullName(eventType);
          public string Unrelated(string csv) => csv.Split(',')[0];
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).IsEmpty().Because("the helper parses; a Split on a value that is not a type name is not the rule's business");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Comparison_OfTwoTypeNameStrings_ReportsWhiz162Async() {
    const string body = """
      namespace App {
        public class Entry { public string EventType { get; set; } = ""; }
        public class Sample {
          public bool Same(Entry e, string eventType) => e.EventType == eventType;
          public bool SameEquals(string clrTypeName, string otherTypeName) => string.Equals(clrTypeName, otherTypeName, StringComparison.Ordinal);
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids.Count(id => id == "WHIZ162")).IsEqualTo(2)
      .Because("a persisted name may be version-decorated; ordinal equality misses its bare form");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Comparison_OfTwoTypes_OrThroughTheMatchingHelper_IsCleanAsync() {
    const string body = """
      namespace App {
        public class Entry { public Type EventType { get; set; } = typeof(int); }
        public class Sample {
          public bool SameType(Entry e, Type eventType) => e.EventType == eventType;
          public bool Normalized(string eventType, string storedEventType) =>
            Whizbang.Core.Messaging.EventTypeMatchingHelper.NormalizeTypeName(eventType) == Whizbang.Core.Messaging.EventTypeMatchingHelper.NormalizeTypeName(storedEventType);
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).IsEmpty().Because("Type identity is exact, and the matching helper normalizes both sides");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task KeyAssignedFromAHandBuiltString_ReportsWhiz163Async() {
    const string body = """
      namespace App {
        public class Row { public string ClrTypeName { get; set; } = ""; public string EnvelopeType { get; set; } = ""; }
        public class Sample {
          public Row Build(Type t, string ns, string name) {
            var row = new Row();
            row.ClrTypeName = $"{ns}.{name}";
            row.EnvelopeType = "MessageEnvelope`1[[" + t.FullName + "]]";
            var eventType = $"{ns}.{name}, {ns}";
            return new Row { ClrTypeName = eventType };
          }
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids.Count(id => id == "WHIZ163")).IsEqualTo(3)
      .Because("the two assignments and the local are keys built by hand; the initializer from a variable is a pass-through");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task KeyAssignedFromAHelper_ALiteral_OrAnotherKey_IsCleanAsync() {
    const string body = """
      namespace App {
        public class Row { public string ClrTypeName { get; set; } = ""; public int TypeNameLength { get; set; } }
        public class Sample {
          public Row Build(Type t, Row other) {
            var row = new Row();
            row.ClrTypeName = Whizbang.Core.TypeNameFormatter.FormatClrTypeName(t);
            row.ClrTypeName = "TestApp.Fixed";
            row.ClrTypeName = other.ClrTypeName;
            row.TypeNameLength = row.ClrTypeName.Length + 1;
            return row;
          }
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).IsEmpty().Because("a helper result, a literal, and another key are the three legitimate sources; an int is not a key");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task InsideTheHelpersThemselves_EverythingIsAllowedAsync() {
    const string body = """
      namespace App {
        public static class TypeNameFormatter {
          public static string Wire(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;
          public static string Bare(string eventType) => eventType.Split(',')[0];
          public static bool Same(string eventType, string otherEventType) => eventType == otherEventType;
        }
        public static class EnvelopeTypeNameHelper {
          public static string Inner(string envelopeTypeName) => envelopeTypeName.Substring(2);
        }
      }
      """;

    var ids = await _idsAsync(body);

    await Assert.That(ids).IsEmpty().Because("the helpers are where the one rendering per form lives");
  }
}
