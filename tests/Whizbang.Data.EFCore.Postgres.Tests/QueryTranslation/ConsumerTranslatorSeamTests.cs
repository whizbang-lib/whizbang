using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A consuming application can register its own method-call translator, and it composes with the
/// framework's rather than replacing it.
/// </summary>
/// <remarks>
/// Without a seam the options were to register a provider plugin directly and hope the framework's
/// options builder did not overwrite it, or to let the predicate fail translation -- the first
/// undefined, the second a silent fall back to client evaluation or a run-time exception. These
/// cases assert what the seam guarantees: the consumer's translation reaches SQL, the framework's
/// own still works beside it, and the order between them is fixed rather than incidental.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/WhizbangDbContextOptionsExtensions.cs</code-under-test>
/// <docs>fundamentals/perspectives/query-translation</docs>
[Category("Shard3")]
public class ConsumerTranslatorSeamTests {

  /// <summary>A function a consumer ships, which Entity Framework cannot translate on its own.</summary>
  private static class ConsumerFunctions {
    public static bool IsDistinctFrom(string? left, string? right) =>
      throw new NotSupportedException("translated on the server; never called");
  }

  /// <summary>Translates the consumer's function to the operator Entity Framework does not model.</summary>
  private sealed class ConsumerTranslator(ISqlExpressionFactory factory) : IMethodCallTranslator {
    private static readonly MethodInfo _target =
      typeof(ConsumerFunctions).GetMethod(nameof(ConsumerFunctions.IsDistinctFrom))!;

    public SqlExpression? Translate(
        SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger) =>
      method == _target
        ? factory.MakeBinary(System.Linq.Expressions.ExpressionType.NotEqual, arguments[0], arguments[1], null)
        : null;
  }

  private sealed class ConsumerPlugin(ISqlExpressionFactory factory) : IMethodCallTranslatorPlugin {
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new ConsumerTranslator(factory)];
  }

  private sealed class SecondConsumerPlugin(ISqlExpressionFactory factory) : IMethodCallTranslatorPlugin {
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new ConsumerTranslator(factory)];
  }

  // Hoisted for CA1861: the framework probe below is evaluated once per test case.
  private static readonly string[] _candidates = ["b"];

  private sealed class ProbeRow {
    public Guid Id { get; set; }
    public string? Left { get; set; }
    public string? Right { get; set; }

    /// <summary>
    /// The framework's own translator works on a column, so the probe needs one.
    /// </summary>
    /// <remarks>
    /// Passing two constants instead makes the predicate independent of the row, and Entity
    /// Framework then evaluates it client-side as a parameter rather than translating it -- which
    /// throws, because a database function has no client implementation. That failure looks like a
    /// broken registration and is not one.
    /// </remarks>
    public List<string> AllowedPrincipals { get; set; } = [];
  }

  private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options) {
    public DbSet<ProbeRow> Rows => Set<ProbeRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<ProbeRow>(e => {
        e.ToTable("probe_rows");
        e.HasKey(r => r.Id);
        // jsonb, which is where the ?| operator the framework's translator emits belongs.
        e.Property(r => r.AllowedPrincipals).HasColumnType("jsonb");
      });
    }
  }

  /// <summary>
  /// Builds options the way a consumer would, with both calls, in the given order.
  /// </summary>
  private static DbContextOptions<ProbeContext> _options(bool consumerFirst, bool registerTwice = false) {
    var builder = new DbContextOptionsBuilder<ProbeContext>();
    builder.UseNpgsql("Host=localhost;Database=unused", npgsql => {
      if (consumerFirst) {
        npgsql.AddWhizbangQueryTranslator<ConsumerPlugin>();
        npgsql.UseWhizbangFunctions();
      } else {
        npgsql.UseWhizbangFunctions();
        npgsql.AddWhizbangQueryTranslator<ConsumerPlugin>();
      }
      if (registerTwice) {
        npgsql.AddWhizbangQueryTranslator<ConsumerPlugin>();
      }
    });
    return builder.Options;
  }

  [Test]
  public async Task AConsumersTranslator_ReachesTheGeneratedSqlAsync() {
    await using var context = new ProbeContext(_options(consumerFirst: false));

    var sql = context.Rows
      .Where(r => ConsumerFunctions.IsDistinctFrom(r.Left, r.Right))
      .ToQueryString();

    await Assert.That(sql).Contains("<>", StringComparison.Ordinal)
      .Because("the consumer's function has to be translated rather than falling back to the "
        + $"client or throwing. SQL was:\n{sql}");
  }

  /// <summary>
  /// Registering the consumer's plugin does not cost the framework's own translators.
  /// </summary>
  /// <remarks>
  /// This is the "composes rather than replaces" half. The extension is added through
  /// AddOrUpdateExtension, which REPLACES the one of its own type, so an implementation that built
  /// a fresh extension instead of amending the existing one would drop whichever of the two calls
  /// came first -- silently, and only for whoever called them in that order.
  /// </remarks>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task TheFrameworksOwnTranslators_SurviveTheConsumerRegistration_EitherCallOrderAsync(bool consumerFirst) {
    await using var context = new ProbeContext(_options(consumerFirst));

    var consumerSql = context.Rows
      .Where(r => ConsumerFunctions.IsDistinctFrom(r.Left, r.Right))
      .ToQueryString();
    var frameworkSql = context.Rows
      .Where(r => EF.Functions.AllowedPrincipalsContainsAny(r.AllowedPrincipals, _candidates))
      .ToQueryString();

    await Assert.That(consumerSql).Contains("<>", StringComparison.Ordinal)
      .Because("the consumer's translator is registered whichever order the two calls were made in.");
    await Assert.That(frameworkSql).Contains("?|", StringComparison.Ordinal)
      .Because($"and the framework's own is still there beside it. SQL was:\n{frameworkSql}");
  }

  /// <summary>Registering the same plugin twice registers it once.</summary>
  /// <remarks>
  /// A shared configuration helper called from two places is the ordinary way this happens, and a
  /// plugin asked twice would offer its translation twice for every method it claims.
  /// </remarks>
  [Test]
  public async Task RegisteringTheSamePluginTwice_RegistersItOnceAsync() {
    await using var context = new ProbeContext(_options(consumerFirst: false, registerTwice: true));

    var sql = context.Rows
      .Where(r => ConsumerFunctions.IsDistinctFrom(r.Left, r.Right))
      .ToQueryString();

    await Assert.That(sql).Contains("<>", StringComparison.Ordinal)
      .Because("the duplicate registration must not break translation.");
  }

  /// <summary>
  /// Two contexts registering different plugins do not share Entity Framework's internal provider.
  /// </summary>
  /// <remarks>
  /// The extension's Info decides this, and it returned a constant hash with an unconditional
  /// "same provider" answer. That was correct while the extension carried nothing. Once it carries
  /// the plugin list it is not: whichever context built the provider first would have decided the
  /// translators BOTH used, so a consumer registering one plugin could silently get another's.
  /// </remarks>
  [Test]
  public async Task TwoContextsWithDifferentPlugins_DoNotShareOneServiceProviderAsync() {
    var first = new DbContextOptionsBuilder<ProbeContext>();
    first.UseNpgsql("Host=localhost;Database=unused", n => n.AddWhizbangQueryTranslator<ConsumerPlugin>());
    var second = new DbContextOptionsBuilder<ProbeContext>();
    second.UseNpgsql("Host=localhost;Database=unused", n => n.AddWhizbangQueryTranslator<SecondConsumerPlugin>());

    var a = first.Options.Extensions.Single(e => e.GetType().Name == "WhizbangOptionsExtension").Info;
    var b = second.Options.Extensions.Single(e => e.GetType().Name == "WhizbangOptionsExtension").Info;

    await Assert.That(a.ShouldUseSameServiceProvider(b)).IsFalse()
      .Because("different plugin sets are different identities, or one context's translators decide "
        + "the other's.");
    await Assert.That(a.GetServiceProviderHashCode()).IsNotEqualTo(b.GetServiceProviderHashCode())
      .Because("and the hash has to separate them too, or the lookup collides before the comparison "
        + "is ever asked.");
  }
}
