using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: <see cref="CascadeContext.WithMetadata(IReadOnlyDictionary{string,object})"/>'s
/// null/empty early return. The only existing test for this overload
/// (<c>WithMetadata_Dictionary_MergesAllEntriesAsync</c>, in Whizbang.Observability.Tests) always
/// passes a non-empty dictionary, so the guard that returns the SAME instance unchanged has never
/// run.
/// </summary>
[Category("Observability")]
public class CascadeContextCoverageTests {

  /// <summary>
  /// Operator impact: an enricher that finds nothing to add must not allocate a new
  /// CascadeContext (or a new Metadata dictionary) on every message just to hand back an
  /// identical copy — that is wasted allocation on the hot cascade path for every message with no
  /// extra metadata to attach.
  /// </summary>
  [Test]
  public async Task WithMetadata_NullDictionary_ReturnsSameInstanceAsync() {
    var original = new CascadeContext { CorrelationId = CorrelationId.New(), CausationId = MessageId.New() };

    var result = original.WithMetadata((IReadOnlyDictionary<string, object>)null!);

    await Assert.That(result).IsSameReferenceAs(original)
      .Because("a null 'nothing to merge' input must short-circuit to the same instance, not "
             + "allocate a pass-through copy");
  }

  /// <summary>Operator impact: same allocation-avoidance guarantee for an explicitly empty dictionary.</summary>
  [Test]
  public async Task WithMetadata_EmptyDictionary_ReturnsSameInstanceAsync() {
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = new Dictionary<string, object> { ["existing"] = "value" },
    };

    var result = original.WithMetadata(new Dictionary<string, object>());

    await Assert.That(result).IsSameReferenceAs(original)
      .Because("an empty 'nothing to merge' input must short-circuit to the same instance, "
             + "leaving existing metadata untouched rather than rebuilding an identical copy");
  }
}
