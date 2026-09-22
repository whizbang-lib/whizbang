using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage tests for <see cref="MarkerInterfaceTransformer"/> paths the primary test suite
/// doesn't reach: completing the interface/struct marker scans past a non-matching declaration,
/// unwrapping a namespace-qualified marker reference, and the base-type extractor's fallback for
/// a syntax shape it doesn't specifically recognize.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/MarkerInterfaceTransformer.cs:*</tests>
public class MarkerInterfaceTransformerCoverageTests {

  // If the interface/struct scan doesn't run to completion for a declaration that doesn't match
  // a marker, a genuine marker interface implementation declared later in the same file could be
  // silently skipped -- the file would keep its stale Wolverine using and never get migrated,
  // with no warning that anything was missed.
  [Test]
  public async Task TransformAsync_NonMarkerInterfaceAndStruct_LeavesFileUnchangedAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public interface IOrderNotifier : IEventHandler {
      }

      public struct OrderMetadata : IEventSink {
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderMetadata.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source)
      .Because("neither declaration implements a Wolverine marker interface by exact name");
    await Assert.That(result.Changes).IsEmpty();
  }

  // If a marker referenced through a namespace-qualified name (common when a shared contracts
  // assembly is imported without a matching using, or to disambiguate a name collision) isn't
  // unwrapped down to its plain interface name, the file is treated as if it implements no
  // marker at all: the Wolverine using survives untouched and the file quietly never migrates.
  [Test]
  public async Task TransformAsync_QualifiedMarkerInterfaceName_SwapsTheUsingAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public record OrderPlaced(string Id) : Contracts.IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;")
      .Because("a namespace-qualified marker reference must still be recognized as IEvent");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.TransformedCode).Contains(": Contracts.IEvent")
      .Because("the qualified base type itself is not this transformer's concern -- only the using is");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingRemoved)).IsTrue();
  }

  // The base-type extractor falls back to raw text for any syntax shape it doesn't specifically
  // unwrap. If that fallback threw, or if it stopped scanning the rest of the base list after
  // hitting an unrecognized entry, a real marker interface listed alongside it would never be
  // found and the file would keep a Wolverine using that no longer resolves once the reference is
  // migrated away.
  [Test]
  public async Task TransformAsync_UnrecognizedBaseListEntryAlongsideAMarker_StillSwapsTheUsingAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public class OrderPlaced : int, IEvent {
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;")
      .Because("scanning must continue past an unrecognized base-list entry to find the real marker");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.TransformedCode).Contains(": int, IEvent")
      .Because("the unrelated base-list entry is not this transformer's concern and must survive untouched");
  }
}
