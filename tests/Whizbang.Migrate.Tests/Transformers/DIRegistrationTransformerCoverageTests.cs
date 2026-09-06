using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage-round tests for <see cref="DIRegistrationTransformer"/> branches not exercised by
/// <see cref="DIRegistrationTransformerTests"/>: method-name detection for a call site that is
/// not a member access at all (a bare identifier, or an invocation on another invocation's
/// result), the method-rename fallback when there is no receiver to rewrite around, and the
/// using-directive step when neither Wolverine nor Marten was ever imported.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/DIRegistrationTransformer.cs:82,83,103,156,232</tests>
public class DIRegistrationTransformerCoverageTests {

  // AddMarten is only rewritten in place when it is called on a receiver; _replaceMethodName
  // falls back to returning the original node when there is none. A bare call -- reachable
  // through 'using static' -- must therefore survive exactly as written rather than come out
  // half-rewritten into something that does not compile.
  [Test]
  public async Task TransformAsync_BareAddMartenCall_IsLeftUnchangedAsync() {
    var transformer = new DIRegistrationTransformer();
    const string sourceCode = """
      using static Marten.MartenServiceCollectionExtensions;

      public class Startup {
        public void Configure(string connectionString) {
          AddMarten(connectionString);
        }
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "Startup.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode)
      .Because("_replaceMethodName only knows how to rewrite a member-access receiver; a bare call must be left byte-for-byte as originally written rather than mangled");
  }

  // Not every invocation's target is a member access or a plain identifier -- calling the result
  // of another call (a factory returning a delegate) is neither. Method-name resolution must
  // report "no name" for that shape, and the rewriter must leave such a call exactly as written
  // rather than crash walking into it. A regression here would either throw partway through a
  // file (losing every other rewrite in it) or silently drop the call.
  [Test]
  public async Task TransformAsync_InvocationOnAnotherInvocationsResult_IsLeftUnchangedAsync() {
    var transformer = new DIRegistrationTransformer();
    const string sourceCode = """
      using Wolverine;

      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      GetConfigureAction()();

      static Action GetConfigureAction() => () => { };
      """;

    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbang()")
      .Because("the genuine Wolverine registration elsewhere in the file must still be rewritten");
    await Assert.That(result.TransformedCode).Contains("GetConfigureAction()();")
      .Because("a call with no resolvable method name has no rewrite rule and must survive untouched");
  }

  // AddWolverine can be recognized and rewritten purely by method name, with no dependency on a
  // matching using directive being present (e.g. it was invoked through a fully-qualified
  // extension method call). When there is no 'using Wolverine;' or 'using Marten;' to consolidate,
  // the using-directive step must be a no-op rather than fabricate a using that was never there.
  [Test]
  public async Task TransformAsync_MethodRewriteWithoutAMatchingUsing_DoesNotAddOneAsync() {
    var transformer = new DIRegistrationTransformer();
    const string sourceCode = """
      var builder = WebApplication.CreateBuilder(args);
      builder.Services.AddWolverine();
      """;

    var result = await transformer.TransformAsync(sourceCode, "Program.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbang()")
      .Because("the method call is still rewritten based on its name alone");
    await Assert.That(result.TransformedCode).DoesNotContain("using Whizbang.Core;")
      .Because("nothing here ever imported Wolverine or Marten, so consolidating a using directive would fabricate an import that was never present");
  }
}
