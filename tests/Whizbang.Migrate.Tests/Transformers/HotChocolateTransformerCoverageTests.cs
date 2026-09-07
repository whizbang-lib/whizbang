using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage-round tests for <see cref="HotChocolateTransformer"/> branches not exercised by
/// <see cref="HotChocolateTransformerTests"/>: method-name detection for a call site that is not
/// a member access at all (a bare identifier, or an invocation on another invocation's result),
/// and the two rewrite paths (method rename, generic-name rename) falling through their
/// respective "not what I expected" fallback when the shape does not match.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/HotChocolateTransformer.cs:109,110,205,262,289</tests>
public class HotChocolateTransformerCoverageTests {

  // AddMartenSorting is only ever recognized when called on a receiver (b.AddMartenSorting()).
  // A bare call -- reachable via 'using static' -- still resolves a method name, but the removal
  // branch requires a MemberAccessExpressionSyntax to rewrite around, so it must decline rather
  // than throw or mangle the call. If detection stopped resolving a name for a bare identifier,
  // this call would not even register as a HotChocolate/Marten pattern, and the file would be
  // skipped by the transformer entirely.
  [Test]
  public async Task TransformAsync_BareAddMartenSortingCall_IsLeftUnchangedAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      public class Startup {
        public void Configure() {
          AddMartenSorting();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source)
      .Because("a bare (non-member-access) AddMartenSorting call is not something the removal rewrite understands");
    await Assert.That(result.Changes).IsEmpty();
  }

  // Not every invocation's target is a member access or a plain identifier -- calling the result
  // of another call (a factory returning a delegate) is neither. Method-name resolution must
  // report "no name" for that shape instead of guessing, and the rewriter must leave such a call
  // exactly as written rather than crash walking into it. A regression here would either throw
  // partway through a file (losing every other rewrite in it) or silently drop the call.
  [Test]
  public async Task TransformAsync_InvocationOnAnotherInvocationsResult_IsLeftUnchangedAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenFiltering();
          GetFilter()();
        }

        private static Action GetFilter() => () => { };
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbangLenses()")
      .Because("the genuine filtering call elsewhere in the file must still be rewritten");
    await Assert.That(result.TransformedCode).Contains("GetFilter()();")
      .Because("a call with no resolvable method name has no rewrite rule and must survive untouched");
  }

  // AddMartenFiltering -> AddWhizbangLenses only rewrites a member-access call; _replaceMethodName
  // falls back to returning the original node when there is no receiver to rewrite around. A bare
  // call reachable through 'using static' must therefore come out of the file exactly as written,
  // not half-rewritten into something that fails to compile.
  [Test]
  public async Task TransformAsync_BareAddMartenFilteringCall_IsLeftUnchangedAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      using static HotChocolate.Data.Marten.MartenFilteringExtensions;

      public class Startup {
        public void Configure() {
          AddMartenFiltering();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source)
      .Because("_replaceMethodName only knows how to rewrite a member-access receiver; a bare call must be left byte-for-byte as originally written rather than mangled");
  }

  // IMartenQueryable is the only generic name this transformer rewrites. An unrelated generic
  // type declared in the same file (which the rewriter still visits, since it walks the whole
  // tree once a Marten pattern is found anywhere) must fall through untouched. If the fallback
  // stopped delegating to the base visitor, an unrelated generic type could be dropped or
  // corrupted purely because the file also happens to use HotChocolate/Marten elsewhere.
  [Test]
  public async Task TransformAsync_UnrelatedGenericTypeAlongsideARewrite_SurvivesUnchangedAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class OrderQueries {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenFiltering();
        }

        public List<int> GetIds() => new();
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderQueries.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbangLenses()")
      .Because("the genuine filtering call elsewhere in the file must still be rewritten");
    await Assert.That(result.TransformedCode).Contains("List<int>")
      .Because("an unrelated generic type must not be touched by the IMartenQueryable rewrite");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.TypeRename)).IsFalse()
      .Because("no generic-name rename should be recorded when nothing named IMartenQueryable appears in the file");
  }
}
