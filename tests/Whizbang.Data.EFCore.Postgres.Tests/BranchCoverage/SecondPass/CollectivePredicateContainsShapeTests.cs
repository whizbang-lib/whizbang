// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The collective predicate compiler's <c>Contains</c> shapes in one class: the three-argument form with
/// no comparer (as a null constant and as a default expression) and with a custom comparer, the shapes it
/// refuses with and without arguments to name, and the values it binds for an <c>IN</c> list (a null, an
/// enum and plain text). No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectivePredicateSqlCompiler.cs</code-under-test>
[Category("Unit")]
[Category("Shard2")]
public class CollectivePredicateContainsShapeTests {

  // Only the default comparer keeps plain equality, which is what SQL IN means. A null comparer (a
  // literal null or a default expression) compiles; a custom one is refused rather than ignored.
  [Test]
  public async Task ThreeArgumentContains_CompilesOnlyWithoutACustomComparerAsync() {
    int[] values = [3, 5];
    Expression<Func<PerspectiveRow<JobModel>, bool>> literalNull = row => Holder.Contains(values, row.Data.Count, null);
    Expression<Func<PerspectiveRow<JobModel>, bool>> custom =
      row => Holder.Contains(values, row.Data.Count, EqualityComparer<int>.Default);

    var fromLiteralNull = CollectivePredicateSqlCompiler<JobModel>.Compile(literalNull);
    var fromDefault = CollectivePredicateSqlCompiler<JobModel>.Compile(_withDefaultComparer(values));

    await Assert.That(fromLiteralNull.SqlFragment).Contains(" IN (");
    await Assert.That(fromLiteralNull.Parameters.Count).IsEqualTo(2);
    await Assert.That(fromDefault.SqlFragment).IsEqualTo(fromLiteralNull.SqlFragment)
      .Because("a default(IEqualityComparer) argument is the same request for plain equality as a literal null");
    await Assert.That(() => CollectivePredicateSqlCompiler<JobModel>.Compile(custom))
      .Throws<NotSupportedException>()
      .WithMessageContaining("custom IEqualityComparer");
  }

  // A Contains of a shape the compiler does not know is refused, and the message names the argument types
  // it saw, or "-" where there is no argument to name.
  [Test]
  public async Task UnsupportedContainsShape_NamesTheArgumentsItSawAsync() {
    var holder = new Holder();
    Expression<Func<PerspectiveRow<JobModel>, bool>> noArguments = row => holder.Contains();
    Expression<Func<PerspectiveRow<JobModel>, bool>> twoArguments = row => holder.Contains(row.Data.Count, 1);

    await Assert.That(() => CollectivePredicateSqlCompiler<JobModel>.Compile(noArguments))
      .Throws<NotSupportedException>()
      .WithMessageContaining("Args=0 [-, -]");
    await Assert.That(() => CollectivePredicateSqlCompiler<JobModel>.Compile(twoArguments))
      .Throws<NotSupportedException>()
      .WithMessageContaining("Args=2 [Int32, Int32]");
  }

  // Each IN value is bound as the text a jsonb ->> extraction yields: a null stays null, an enum is its
  // number (how the document stores it), and anything else is its own text.
  [Test]
  public async Task InListValues_AreBoundAsTheirStoredTextAsync() {
    string?[] names = [null, "north"];
    JobKind[] kinds = [JobKind.Urgent];
    Expression<Func<PerspectiveRow<JobModel>, bool>> byName = row => names.Contains(row.Data.Name);
    Expression<Func<PerspectiveRow<JobModel>, bool>> byKind = row => kinds.Contains(row.Data.Kind);

    var nameClause = CollectivePredicateSqlCompiler<JobModel>.Compile(byName);
    var kindClause = CollectivePredicateSqlCompiler<JobModel>.Compile(byKind);

    await Assert.That(nameClause.Parameters["where_name_0"]).IsNull();
    await Assert.That(nameClause.Parameters["where_name_1"]).IsEqualTo("north");
    await Assert.That(kindClause.Parameters["where_kind_0"]).IsEqualTo("1")
      .Because("a plain enum is stored as its underlying number, so its name would never match");
  }

  private static Expression<Func<PerspectiveRow<JobModel>, bool>> _withDefaultComparer(int[] values) {
    var row = Expression.Parameter(typeof(PerspectiveRow<JobModel>), "row");
    var count = Expression.Property(Expression.Property(row, nameof(PerspectiveRow<JobModel>.Data)), nameof(JobModel.Count));
    var method = typeof(Holder).GetMethod(nameof(Holder.Contains), [typeof(int[]), typeof(int), typeof(IEqualityComparer<int>)])!;
    var call = Expression.Call(method, Expression.Constant(values), count, Expression.Default(typeof(IEqualityComparer<int>)));
    return Expression.Lambda<Func<PerspectiveRow<JobModel>, bool>>(call, row);
  }

  internal enum JobKind {
    Routine = 0,
    Urgent = 1,
  }

  internal sealed class JobModel {
    public string? Name { get; set; }
    public int Count { get; set; }
    public JobKind Kind { get; set; }
  }

  /// <summary>Methods named Contains that the compiler meets by name; never called.</summary>
  private sealed class Holder {
    public static bool Contains(int[] values, int item, IEqualityComparer<int>? comparer) =>
      throw new NotSupportedException($"{values.Length}, {item}, {comparer}");

    public bool Contains() => throw new NotSupportedException(GetType().Name);

    public bool Contains(int item, int other) => throw new NotSupportedException($"{GetType().Name}: {item}, {other}");
  }
}
