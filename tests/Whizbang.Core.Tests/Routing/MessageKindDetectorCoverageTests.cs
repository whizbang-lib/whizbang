using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Targeted coverage for <see cref="MessageKindDetector"/>'s null/empty-<c>Type.Namespace</c>
/// guards — reached only by a type with no namespace at all. The broader
/// <see cref="MessageKindDetectorTests"/> suite's <c>Detect_EmptyNamespaceType_UsesOtherDetectionAsync</c>
/// test uses <c>typeof(string)</c>, but <c>string.Namespace</c> is <c>"System"</c> (not
/// null/empty), so it never actually exercises these branches. An anonymous type's
/// <c>Type.Namespace</c> genuinely is <c>null</c> — it is compiler-generated at the global
/// namespace — which is what these guards exist to handle without throwing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/MessageKindDetector.cs</code-under-test>
public class MessageKindDetectorCoverageTests {

  [Test]
  public async Task Detect_TypeWithNoNamespace_FallsThroughToUnknownWithoutThrowingAsync() {
    // A message-kind classifier that throws (or NullReferenceExceptions) on a namespace-less type
    // instead of falling through would break any diagnostic/tooling call site that classifies an
    // arbitrary runtime Type — the classifier is documented as metadata/tooling-only precisely so
    // it can be pointed at whatever type shows up, not just well-formed message types.
    var namespaceLessType = new { }.GetType();

    await Assert.That(namespaceLessType.Namespace).IsNull()
      .Because("an anonymous type is compiler-generated with no namespace — the precondition this test needs");

    var result = MessageKindDetector.Detect(namespaceLessType);

    await Assert.That(result).IsEqualTo(MessageKind.Unknown)
      .Because("no attribute, no framework-system namespace, no interface, no namespace, and no recognized name suffix — every layer must decline gracefully");
  }
}
