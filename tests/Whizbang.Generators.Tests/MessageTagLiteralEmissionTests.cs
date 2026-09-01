using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// How <see cref="MessageTagDiscoveryGenerator"/> turns an attribute's named-argument values back
/// into C# literals for the generated registry.
/// </summary>
/// <remarks>
/// A tag attribute is written by the consumer and can carry any constant a C# attribute allows.
/// The generator has to re-emit each of those as source, which is a round trip through a
/// representation that does not have to survive: a string with a quote in it, an enum member that
/// was a <c>[Flags]</c> combination with no name, a <c>typeof</c>, an array.
///
/// <para>
/// Getting one wrong produces a registry that does not compile — in a generated file the consumer
/// cannot edit, reported against a line they did not write. So the bar here is not "the value is
/// preserved" but "the emitted text is valid C# that means the same thing", and the last test
/// checks exactly that by compiling the result.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/MessageTagDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class MessageTagLiteralEmissionTests {

  /// <summary>A consumer-defined tag attribute carrying one named argument of each kind.</summary>
  private const string CUSTOM_ATTRIBUTE = """
    public enum Severity { Low = 1, High = 2 }

    [System.Flags]
    public enum Channels { None = 0, Email = 1, Sms = 2, Push = 4 }

    public sealed class AuditTagAttribute : Whizbang.Core.Attributes.MessageTagAttribute {
      public string? Note { get; init; }
      public bool Sensitive { get; init; }
      public int Retention { get; init; }
      public char Marker { get; init; }
      public double Weight { get; init; }
      public Severity Level { get; init; }
      public Channels Notify { get; init; }
      public System.Type? Handler { get; init; }
      public string[]? Labels { get; init; }
    }
    """;

  private static string _sourceWith(string attributeUsage) => $$"""
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      {{CUSTOM_ATTRIBUTE}}

      public sealed class SomeHandler { }

      {{attributeUsage}}
      public record AuditedEvent(Guid Id);
      """;

  private static string _registryFor(string attributeUsage) {
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(
      _sourceWith(attributeUsage));
    return GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs") ?? string.Empty;
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StringArgument_IsEmittedQuotedAsync() {
    var registry = _registryFor("""[AuditTag(Tag = "audited", Note = "kept for 7 years")]""");

    await Assert.That(registry).Contains("\"kept for 7 years\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StringArgumentWithAQuote_IsEscapedAsync() {
    // The failure that actually happens: an apostrophe or quote in a human-written note closes
    // the literal early and the generated file stops compiling.
    var registry = _registryFor("""[AuditTag(Tag = "audited", Note = "the \"primary\" record")]""");

    await Assert.That(registry).DoesNotContain("Note = \"the \"primary\" record\"")
      .Because("an unescaped quote closes the literal early and breaks the generated file");
    await Assert.That(registry).Contains("\\\"primary\\\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("true")]
  [Arguments("false")]
  public async Task BoolArgument_IsEmittedAsAKeywordAsync(string value) {
    // ToString() on a bool gives "True"/"False", which are not C# keywords — emitting those
    // would not compile.
    var registry = _registryFor($$"""[AuditTag(Tag = "audited", Sensitive = {{value}})]""");

    await Assert.That(registry).Contains($"Sensitive = {value}");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task CharArgument_IsEmittedInSingleQuotesAsync() {
    var registry = _registryFor("""[AuditTag(Tag = "audited", Marker = 'x')]""");

    await Assert.That(registry).Contains("'x'");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NumericArguments_AreEmittedBareAsync() {
    var registry = _registryFor("""[AuditTag(Tag = "audited", Retention = 7, Weight = 1.5)]""");

    await Assert.That(registry).Contains("Retention = 7");
    await Assert.That(registry).Contains("Weight = 1.5");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EnumArgument_IsEmittedAsACastAsync() {
    // Emitted as ((EnumType)value) rather than by member name, so it survives the next case too.
    var registry = _registryFor("""[AuditTag(Tag = "audited", Level = Severity.High)]""");

    await Assert.That(registry).Contains("Severity");
    await Assert.That(registry).Contains("2");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FlagsCombinationWithNoMemberName_StillEmitsAsync() {
    // Email|Sms is 3, which no member is named. Emitting a member name would be impossible
    // here — the cast form is what makes this case work at all.
    var registry = _registryFor(
      """[AuditTag(Tag = "audited", Notify = Channels.Email | Channels.Sms)]""");

    await Assert.That(registry).Contains("Channels");
    await Assert.That(registry).Contains("3")
      .Because("a [Flags] combination has no member name — only the cast form can express it");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TypeArgument_IsEmittedAsTypeofAsync() {
    var registry = _registryFor("""[AuditTag(Tag = "audited", Handler = typeof(SomeHandler))]""");

    await Assert.That(registry).Contains("typeof(");
    await Assert.That(registry).Contains("SomeHandler");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ArrayArgument_IsEmittedAsAnArrayCreationAsync() {
    var registry = _registryFor(
      """[AuditTag(Tag = "audited", Labels = new[] { "alpha", "beta" })]""");

    await Assert.That(registry).Contains("alpha");
    await Assert.That(registry).Contains("beta");
    await Assert.That(registry).Contains("[]");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NullArgument_IsEmittedAsNullAsync() {
    var registry = _registryFor("""[AuditTag(Tag = "audited", Note = null)]""");

    await Assert.That(registry).Contains("null");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheDedicatedSlotsAreNotDuplicatedIntoExtrasAsync() {
    // Tag has its own slot on MessageTagInfo. Emitting it again as an extra initializer would
    // assign the same property twice in one object initializer, which does not compile.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<MessageTagDiscoveryGenerator>(
      _sourceWith("""[AuditTag(Tag = "audited", Note = "n")]"""));

    await Assert.That(errors.Any(d => d.Id == "CS1912")).IsFalse()
      .Because("assigning the same property twice in one initializer is a compile error");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryKindTogether_ProducesCompilableRegistryAsync() {
    // The real bar: the emitted text has to be valid C#, in a file the consumer cannot edit.
    var source = $$"""
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      {{CUSTOM_ATTRIBUTE}}

      public sealed class SomeHandler { }

      [AuditTag(
        Tag = "audited",
        Note = "a \"quoted\" note",
        Sensitive = true,
        Retention = 7,
        Marker = 'x',
        Weight = 1.5,
        Level = Severity.High,
        Notify = Channels.Email | Channels.Sms,
        Handler = typeof(SomeHandler),
        Labels = new[] { "alpha", "beta" })]
      public record AuditedEvent(Guid Id);
      """;

    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<MessageTagDiscoveryGenerator>(source);

    await Assert.That(errors).IsEmpty()
      .Because("a registry that does not compile is reported against a file the consumer never "
             + "wrote and cannot edit — so the emitted text has to be valid C#, not merely "
             + "diagnostic-free");
  }
}
