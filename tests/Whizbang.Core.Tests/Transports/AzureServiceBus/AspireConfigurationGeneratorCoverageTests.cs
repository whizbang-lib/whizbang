using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports.AzureServiceBus;

namespace Whizbang.Core.Tests.Transports.AzureServiceBus;

/// <summary>
/// Covers <c>AspireConfigurationGenerator._toCamelCase</c>'s two early-return edges via the
/// public <see cref="AspireConfigurationGenerator.GenerateAppHostCode"/> entry point: a
/// whitespace-only topic name, and a topic name made ENTIRELY of the '-'/'_' separator
/// characters (so splitting on them yields zero parts). Neither shape appears in
/// <c>AspireConfigurationGeneratorTests</c>.
/// </summary>
public class AspireConfigurationGeneratorCoverageTests {

  /// <summary>
  /// If this guard were removed, indexing the first character of an empty/whitespace topic name
  /// while building the camelCase variable name would throw — one malformed topic requirement
  /// would take down code generation for every OTHER topic in the same call instead of emitting
  /// (admittedly unusable) code an operator can see and fix.
  /// </summary>
  [Test]
  public async Task GenerateAppHostCode_WhitespaceOnlyTopicName_EmitsItVerbatimRatherThanThrowingAsync() {
    var code = AspireConfigurationGenerator.GenerateAppHostCode([
      new TopicRequirement("   ", "sub-1"),
    ]);

    await Assert.That(code).Contains("var    Topic = serviceBus.AddServiceBusTopic(\"   \");")
      .Because("a whitespace-only topic name is not a valid variable name fragment — the generator "
             + "must not throw building it, even though the resulting declaration is not usable "
             + "C# until the operator fixes the topic name itself");
  }

  /// <summary>
  /// If this guard were removed, a topic name made entirely of hyphens/underscores would split
  /// into zero parts and then throw indexing <c>parts[0]</c> — again taking down generation for
  /// every other topic in the same call.
  /// </summary>
  [Test]
  public async Task GenerateAppHostCode_TopicNameIsOnlySeparatorCharacters_EmitsItVerbatimRatherThanThrowingAsync() {
    var code = AspireConfigurationGenerator.GenerateAppHostCode([
      new TopicRequirement("---", "sub-1"),
    ]);

    await Assert.That(code).Contains("var ---Topic = serviceBus.AddServiceBusTopic(\"---\");")
      .Because("a topic name of only separator characters splits into zero parts — the generator "
             + "must not throw building the variable name, even though the resulting declaration "
             + "is not usable C# until the operator fixes the topic name itself");
  }
}
