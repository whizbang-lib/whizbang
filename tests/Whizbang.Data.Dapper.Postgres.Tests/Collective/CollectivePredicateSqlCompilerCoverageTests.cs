#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the one <see cref="CollectivePredicateSqlCompiler{TModel}"/> arm
/// not already locked by <see cref="CollectivePredicateSqlCompilerTests"/>: a boolean-valued
/// method call that is neither <c>Any</c> nor <c>Contains</c>. This compiler turns a collective
/// apply's predicate into SQL with no further validation downstream — a shape it does not
/// recognize must throw rather than fall through and emit no WHERE clause fragment at all
/// (which would UPDATE every row the caller never asked for).
/// </summary>
public class CollectivePredicateSqlCompilerCoverageTests {

  private sealed class JobModel {
    public string Status { get; set; } = "";
  }

  [Test]
  public async Task Compile_UnsupportedBooleanMethodCall_ThrowsNotSupportedAsync() {
    // StartsWith is a method call returning bool, but it is neither the `Any` cross-perspective
    // shape nor the `Contains` IN-clause shape — it must fall through both recognized arms and
    // hit the compiler's catch-all throw, not silently compile to nothing.
    // Two characters deliberately: StartsWith("A") trips CA1866 (prefer the char overload), and
    // switching to the char overload would change which MethodInfo the compiler sees. A
    // multi-character prefix keeps the unsupported-string-method shape this test is about.
    Expression<Func<PerspectiveRow<JobModel>, bool>> filter = row => row.Data.Status.StartsWith("Ar", StringComparison.Ordinal);

    await Assert.That(() => CollectivePredicateSqlCompiler<JobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  /// <summary>An id compared with nothing binds nothing, rather than binding the text "".</summary>
  /// <remarks>
  /// The id column is a real uuid and takes the guid itself, so the conversion every other column's
  /// value goes through is skipped for it — which means the null case has to be handled on the way
  /// past. Bound as the empty string instead, Postgres refuses the statement (22P02, invalid input
  /// syntax for uuid), so the apply fails rather than matching no rows.
  /// </remarks>
  [Test]
  public async Task Compile_IdComparedWithNull_BindsNullRatherThanTextAsync() {
    Guid? nothing = null;
    Expression<Func<PerspectiveRow<JobModel>, bool>> filter = row => row.Id == nothing;

    var result = CollectivePredicateSqlCompiler<JobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("id = @where_id");
    await Assert.That(result.Parameters["where_id"]).IsNull()
      .Because("null is the absence of a value, and the uuid column takes it as one");
  }

  /// <summary>An id held as text is parsed into the guid the uuid column compares against.</summary>
  /// <remarks>
  /// A caller whose ids arrive as strings — from a request, a config file, a message body — compares
  /// them to the id column without converting first, and means the id. Passed along as text the whole
  /// statement is refused (42883, no operator uuid = text), so the string is parsed here instead.
  /// </remarks>
  [Test]
  public async Task Compile_IdComparedWithItsText_ParsesItToTheGuidAsync() {
    var wanted = Guid.NewGuid();
    object[] asText = [wanted.ToString()];
    Expression<Func<PerspectiveRow<JobModel>, bool>> filter = row => asText.Contains(row.Id);

    var result = CollectivePredicateSqlCompiler<JobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("id IN (@where_id_0)");
    await Assert.That(result.Parameters["where_id_0"]).IsEqualTo(wanted)
      .Because("a caller comparing an id to a string means the id, and the column takes a guid");
  }

  /// <summary>Anything else compared with an id says so, at compile time rather than in Postgres.</summary>
  /// <remarks>
  /// A number or another type reaching the uuid column cannot be what the caller meant, and the
  /// alternative to refusing it here is a statement Postgres refuses at apply time with a message
  /// about operators rather than about the predicate that produced it.
  /// </remarks>
  [Test]
  public async Task Compile_IdComparedWithSomethingElse_SaysWhatTheColumnTakesAsync() {
    object[] notIds = [42];
    Expression<Func<PerspectiveRow<JobModel>, bool>> filter = row => notIds.Contains(row.Id);

    var thrown = await Assert.That(() => CollectivePredicateSqlCompiler<JobModel>.Compile(filter))
      .Throws<NotSupportedException>();

    await Assert.That(thrown!.Message).Contains("uuid", StringComparison.Ordinal)
      .Because("the message names what the column takes, so the caller can see what to pass instead");
    await Assert.That(thrown.Message).Contains("Int32", StringComparison.Ordinal)
      .Because("and what arrived, so they can see which value in the predicate is the wrong one");
  }

}
