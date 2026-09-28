using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated message JSON context names each property the way System.Text.Json would: the
/// <c>[JsonPropertyName]</c> value when the property carries one, and the C# name otherwise.
/// Before this, the attribute was ignored and every property went out under its C# name, so a type
/// written to match an external schema serialized differently under the generated context than under
/// a plain serializer.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextPropertyNameTests {

  /// <summary>
  /// Matches the emitted <c>CreateProperty</c> call for one property: its C# name, then its JSON name.
  /// </summary>
  private static bool _emitsProperty(string code, string propertyName, string jsonName) =>
    Regex.IsMatch(code,
      $@"CreateProperty<[^>]+>\(\s*options,\s*""{Regex.Escape(propertyName)}"",\s*""{Regex.Escape(jsonName)}""",
      RegexOptions.None, TimeSpan.FromSeconds(5));

  /// <summary>
  /// The write filter assigned right after the property's <c>CreateProperty</c> call (the text after
  /// <c>ShouldSerialize = </c>), or <see langword="null"/> when none is assigned.
  /// </summary>
  private static string? _writeFilterFor(string code, string propertyName) {
    var match = Regex.Match(code,
      $@"CreateProperty<[^>]+>\(\s*options,\s*""{Regex.Escape(propertyName)}"",[^;]*;\s*(?:properties\[\d+\]\.ShouldSerialize = ([^\r\n]*))?",
      RegexOptions.None, TimeSpan.FromSeconds(5));
    return match.Groups[1].Success ? match.Groups[1].Value : null;
  }

  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    return code!;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PositionalRecordWithJsonPropertyName_EmitsTheAttributeNameAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public record ItemsCounted([property: JsonPropertyName("n")] int Count, string Label) : IEvent;
      """);

    await Assert.That(_emitsProperty(code, "Count", "n")).IsTrue()
      .Because("the attribute names the property on the wire, as it does for System.Text.Json itself");
    await Assert.That(_emitsProperty(code, "Label", "Label")).IsTrue()
      .Because("a property without the attribute keeps its C# name, so existing wire data is unchanged");
    await Assert.That(code).Contains("Name = \"Count\"")
      .Because("constructor parameters still bind by their C# name; only the wire name changes");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ClassPropertyWithJsonPropertyName_EmitsTheAttributeNameAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class RenameCatalog : ICommand {
        [JsonPropertyName("cid")]
        public string CatalogId { get; init; } = "";
        public string NewName { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "CatalogId", "cid")).IsTrue();
    await Assert.That(_emitsProperty(code, "NewName", "NewName")).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InheritedAndNestedPropertiesWithJsonPropertyName_EmitTheAttributeNameAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class Line {
        [JsonPropertyName("q")]
        public int Quantity { get; set; }
      }
      public abstract class AuditedEvent {
        [JsonPropertyName("by")]
        public string ChangedBy { get; set; } = "";
      }
      public class LinesChanged : AuditedEvent, IEvent {
        public Line[] Lines { get; set; } = [];
      }
      """);

    await Assert.That(_emitsProperty(code, "ChangedBy", "by")).IsTrue()
      .Because("an inherited property carries its declared attribute into the derived message");
    await Assert.That(_emitsProperty(code, "Quantity", "q")).IsTrue()
      .Because("a type reached through a message property is generated the same way as the message");
    await Assert.That(_emitsProperty(code, "Lines", "Lines")).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_JsonPropertyNameNeedingEscapes_IsEmittedAsAValidLiteralAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class LabelSet : ICommand {
        [JsonPropertyName("say \"hi\"\\now")]
        public string Greeting { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Greeting", @"say \""hi\""\\now")).IsTrue()
      .Because("the name is data inside a generated string literal; an unescaped quote would break the build");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_OverrideWithoutTheAttribute_KeepsItsCSharpNameAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class NamedBase {
        [JsonPropertyName("t")]
        public virtual string Title { get; set; } = "";
      }
      public class Retitled : NamedBase, ICommand {
        public override string Title { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Title", "Title")).IsTrue()
      .Because("System.Text.Json reads the attribute from the property as declared on the type, not from the member it overrides");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_JsonIgnoredProperty_IsNotEmittedAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class PriceChanged : IEvent {
        public decimal Price { get; set; }
        [JsonIgnore]
        public string DisplayPrice { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Price", "Price")).IsTrue();
    await Assert.That(code).DoesNotContain("\"DisplayPrice\"")
      .Because("an ignored property is left out of the generated contract, as System.Text.Json leaves it out");
  }

  /// <summary>
  /// A conditional <c>[JsonIgnore]</c> keeps the property in the contract and skips it only when
  /// writing a null or default value. Before, every <c>[JsonIgnore]</c> removed the property, so a
  /// value that should have been written whenever it was set was never written at all.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ConditionalJsonIgnore_KeepsThePropertyAndSkipsItOnlyWhenWritingAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class ScopeTagged : IEvent {
        [JsonPropertyName("t")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tenant { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSystem { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Always { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        public string ReadOnlyInput { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
        public string Hidden { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Tenant", "t")).IsTrue();
    await Assert.That(_emitsProperty(code, "IsSystem", "IsSystem")).IsTrue();
    await Assert.That(_emitsProperty(code, "Always", "Always")).IsTrue();
    await Assert.That(_emitsProperty(code, "ReadOnlyInput", "ReadOnlyInput")).IsTrue();
    await Assert.That(code).DoesNotContain("\"Hidden\"")
      .Because("Condition = Always is the same as an unconditional [JsonIgnore]");

    await Assert.That(_writeFilterFor(code, "Tenant")).IsEqualTo("static (_, value) => value is not null;");
    await Assert.That(_writeFilterFor(code, "IsSystem")).IsEqualTo(
      "static (_, value) => !global::System.Collections.Generic.EqualityComparer<bool>.Default.Equals((bool)value!, default!);");
    await Assert.That(_writeFilterFor(code, "Always")).IsNull()
      .Because("Condition = Never always writes the property");
    await Assert.That(_writeFilterFor(code, "ReadOnlyInput")).IsEqualTo("static (_, _) => false;");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_JsonIgnoreWithAnUndefinedCondition_IsTreatedAsAlwaysAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class OddlyIgnored : IEvent {
        public string Kept { get; set; } = "";
        [JsonIgnore(Condition = (JsonIgnoreCondition)42)]
        public string Dropped { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Kept", "Kept")).IsTrue();
    await Assert.That(code).DoesNotContain("\"Dropped\"")
      .Because("a condition the generator cannot name is read as the attribute's plain meaning: ignore");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WhenReadingCondition_KeepsThePropertyWithoutAWriteFilterAsync() {
    var code = await _generateAsync("""
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class Reported : IEvent {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
        public string Summary { get; set; } = "";
      }
      """);

    await Assert.That(_emitsProperty(code, "Summary", "Summary")).IsTrue();
    await Assert.That(_writeFilterFor(code, "Summary")).IsNull()
      .Because("WhenReading does not affect writing");
  }
}
