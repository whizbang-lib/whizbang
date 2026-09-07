using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="TopicRegistryGenerator"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/TopicRegistryGeneratorTests.cs</c>. That file only exercises
/// <c>[Topic]</c> attributes carrying a real string literal; this targets the branch where
/// <c>_resolveBaseTopic</c> finds a <c>[Topic]</c> attribute but cannot read a topic name out of it,
/// which is the only way <c>_extractTopicInfo</c>'s <c>baseTopic is null</c> guard can be reached
/// (the convention-based fallback path always produces a non-null string).
/// </summary>
[Category("SourceGenerators")]
public class TopicRegistryGeneratorCoverageTests {

  /// <summary>
  /// A type decorated <c>[Topic(null)]</c> compiles (the argument is a compile-time constant; the
  /// constructor's null-guard only runs if the attribute is ever instantiated, which Roslyn never
  /// does for <c>AttributeData</c> inspection). If the registry generator started treating a null
  /// constructor-argument value as a topic name instead of dropping the type, every consumer would
  /// see a routed message land in a literal <c>"null"</c>-named topic instead of being silently
  /// excluded from the registry.
  /// </summary>
  [Test]
  public async Task Generator_WithNullTopicAttributeArgument_ExcludesTypeFromRegistryAsync() {
    // Arrange — one event with a real topic name (so the registry is actually generated) and one
    // whose [Topic] attribute carries a null constructor argument.
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic("products")]
      public record ProductCreatedEvent : IEvent;

      [Topic(null)]
      public record UntopicedEvent : IEvent;

    """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert — no errors; the null-argument type does not abort generation for the rest.
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)")
      .Because("the type with a real topic name must still be registered");
    await Assert.That(registryCode).DoesNotContain("UntopicedEvent")
      .Because("[Topic(null)] resolves to a null base topic (the constructor-argument value fails the `is string` check), and _extractTopicInfo's baseTopic-null guard must drop the type before code generation rather than emitting a broken entry");
  }
}
