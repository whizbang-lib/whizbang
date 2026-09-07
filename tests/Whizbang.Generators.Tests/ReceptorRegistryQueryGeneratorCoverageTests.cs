using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="ReceptorRegistryQueryGenerator"/> targeting the
/// notification-tag discovery branches (<c>[NotificationTag]</c> / <c>[NotificationIdTag]</c>) that
/// <c>ReceptorRegistryQueryGeneratorTests.cs</c> does not exercise. That file already covers the
/// composite/collective abstract-exclusion pattern this mirrors.
/// </summary>
/// <remarks>
/// The generator matches tag attributes by fully-qualified NAME
/// (<c>Whizbang.Core.NotificationTagAttribute</c> / <c>Whizbang.Core.NotificationIdTagAttribute</c>),
/// not by a real type reference into Whizbang.Core — no such attribute ships in Whizbang.Core today
/// (the framework only checks the name a consumer's own attribute would carry, per the doc example in
/// <c>Whizbang.Core.Attributes.AttributeArgNamingAttribute</c>). These tests declare a matching type
/// under that namespace directly in the compiled test source, which is exactly how a consuming
/// application would satisfy the check.
/// </remarks>
[Category("SourceGenerators")]
[Category("ReceptorRegistryQuery")]
public class ReceptorRegistryQueryGeneratorCoverageTests {

  /// <summary>
  /// An abstract type can never be the concrete runtime type of a dispatched message, so tagging one
  /// with [NotificationTag] must not register it as a consumer — an abstract type in AnyConsumerTypes
  /// is dead weight that can never actually arrive at the receive boundary.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractTypeWithNotificationTag_NotRegisteredAsConsumerAsync() {
    const string source = @"
namespace Whizbang.Core {
  public sealed class NotificationTagAttribute : System.Attribute {
    public NotificationTagAttribute(string tag) { }
  }
}

namespace MyApp {
  [Whizbang.Core.NotificationTagAttribute(""legacy"")]
  public abstract class AbstractTaggedNotice { }
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).DoesNotContain("AbstractTaggedNotice")
      .Because("an abstract type can never be the concrete runtime type of a dispatched message and must not be registered as a consumer just because it carries [NotificationTag]");
  }

  /// <summary>
  /// A message type decorated with [NotificationIdTag] has a downstream consumer (the SignalR
  /// tagged-notification dispatcher) even though it has no receptor and no perspective. Without this
  /// registration, HasAnyConsumer would report false and the receive-boundary drop-gate would discard
  /// the message before the tagged-notification dispatcher ever sees it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ConcreteTypeWithNotificationIdTag_HasAnyConsumerReturnsTrueAsync() {
    const string source = @"
namespace Whizbang.Core {
  public sealed class NotificationIdTagAttribute : System.Attribute {
    public NotificationIdTagAttribute(string tag) { }
  }
}

namespace MyApp {
  [Whizbang.Core.NotificationIdTagAttribute(""job-progress"")]
  public class JobProgressNotice { }
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    var consumerRegion = _extractRegion(generated!, "AnyConsumerTypes");
    await Assert.That(consumerRegion).Contains("MyApp.JobProgressNotice")
      .Because("a type carrying [NotificationIdTag] has a downstream consumer (the tagged-notification "
             + "dispatcher) and must register as a consumer so the receive-boundary drop-gate does not discard it");
  }

  /// <summary>Isolates the braced initializer for one contribution field (e.g. "AnyConsumerTypes")
  /// so assertions are scoped to that field instead of the whole generated file — an over-broad
  /// Contains would pass for the wrong reason (a coincidental match elsewhere in the file).</summary>
  private static string _extractRegion(string source, string fieldName) {
    var fieldStart = source.IndexOf(fieldName, StringComparison.Ordinal);
    if (fieldStart < 0) {
      return string.Empty;
    }
    var braceStart = source.IndexOf('{', fieldStart);
    if (braceStart < 0) {
      return string.Empty;
    }
    var depth = 0;
    for (var i = braceStart; i < source.Length; i++) {
      if (source[i] == '{') {
        depth++;
      } else if (source[i] == '}') {
        depth--;
        if (depth == 0) {
          return source[braceStart..(i + 1)];
        }
      }
    }
    return source[braceStart..];
  }
}
