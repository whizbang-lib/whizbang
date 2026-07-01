using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core driver adapter for collective-event apply (Slice 6'). Takes
/// the perspective's <see cref="ICollectiveSpec{TModel}"/> mutation
/// description and the resolver's scope filter and produces a single
/// <c>ExecuteUpdateAsync</c> call against the perspective table whose
/// <c>WHERE</c> is exactly the scope predicate — nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline:
/// </para>
/// <list type="number">
///   <item><description>Resolve <c>dbContext.Set&lt;PerspectiveRow&lt;TModel&gt;&gt;()</c>.</description></item>
///   <item><description>Compose <c>Where(scopeFilter)</c> from the resolver. That's the SOLE <c>WHERE</c> — there is no matched-id membership clause; the event has no captured matched set (Slice 1' contract change).</description></item>
///   <item><description>Translate the perspective's <c>ICollectiveSetters</c> spec into <c>UpdateSettersBuilder&lt;PerspectiveRow&lt;TModel&gt;&gt;</c> shape via <see cref="CollectiveSettersRewriter"/>, which emits native nested <c>SetProperty(r =&gt; r.Data.&lt;Prop&gt;, value)</c> calls — EF Core 10 updates the <c>ComplexProperty().ToJson()</c> sub-properties directly.</description></item>
///   <item><description>Execute via <c>ExecuteUpdateAsync</c>.</description></item>
/// </list>
/// <para>
/// Returns the count of affected rows so the runner can log / surface
/// it as a metric.
/// </para>
/// <para>
/// <strong>Determinism:</strong> the predicate is re-evaluated at apply
/// time against the projection state at that point in the event sequence.
/// Because event-sourcing guarantees the projection state is fully
/// determined by the event log up to that point, the result is
/// deterministic — and reflects the logically correct outcome, not the
/// original execution's possibly-wrong (e.g. out-of-order delivery) one.
/// </para>
/// <para>
/// AOT: matches Whizbang.Data.EFCore.Postgres's established pattern of
/// suppressing IL2060/IL3050 — EF Core's query translation pipeline is
/// reflection-based by design.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose projection table is mutated.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Adapter is generic over TModel; static factory + execute methods match the pattern of EF Core's own generic-static helpers.")]
public sealed class EFCoreCollectiveAdapter<TModel> where TModel : class {
  /// <summary>
  /// Build the final query + setters pair that <c>ExecuteUpdateAsync</c>
  /// consumes. Exposed separately from <see cref="ExecuteAsync"/> so
  /// unit tests can inspect the composition without needing a connected
  /// DbContext.
  /// </summary>
  internal static (IQueryable<PerspectiveRow<TModel>> Query,
                   Expression<Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<PerspectiveRow<TModel>>>> Setters)
    BuildCall(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter) {

    var query = dbContext.Set<PerspectiveRow<TModel>>().Where(scopeFilter);
    var setters = CollectiveSettersRewriter.Rewrite(spec.Setters);
    return (query, setters);
  }

  /// <summary>
  /// Execute the collective-event mutation as a single SQL UPDATE.
  /// Returns the number of affected rows.
  /// </summary>
  public static Task<int> ExecuteAsync(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(scopeFilter);

    // The native nested SetProperty(r => r.Data.Prop, value) path requires Data to be mapped as an EF Core 10
    // ComplexProperty().ToJson() complex type. It fails when:
    //   • any value is null — EF emits an untyped NULL (Postgres 42804); the value-selector form nulls the
    //     whole column;
    //   • Data is mapped as a SCALAR jsonb column instead of a complex type — the polymorphic mapping a consumer
    //     generates for models with [JsonPolymorphic] members (e.g. OrderTenantFields). There is no complex
    //     sub-property to target, so EF rejects it with "does not represent a valid property to be set".
    // In those cases we fall back to a raw jsonb_set UPDATE that targets the jsonb column directly. The WHERE
    // is still translated by EF (we materialize the matching ids first), so cross-perspective cohorts keep
    // working.
    //
    // The path decision is evaluated ONCE (deterministic, no I/O) so the retry below re-runs only the DB work.
    var assignments = CollectiveSettersRewriter.CollectAssignments(spec.Setters);
    var useRawPath = assignments.Any(a => a.IsNull) || !_dataIsComplexProperty(dbContext);

    // A collective apply is a single set-based UPDATE over every row in scope, so it overlaps row locks with
    // concurrent per-row projection writes and other pods' collective UPDATEs — transient 40P01 (deadlock)
    // and 40001 (serialization_failure) are expected under load. Retry the whole apply in-line with jittered
    // backoff so contention clears here, before a failure ever bubbles up to the __collective__ sink's attempt
    // counter. That ordering matters: a deadlock is transient, not poison, so it must resolve via retry — if
    // it instead incremented the sink attempts it could eventually (and wrongly) dead-letter a valid event.
    // Re-materializing ids on the raw path each attempt is idempotent.
    return PostgresDeadlockRetry.ExecuteAsync(
      () => useRawPath
        ? _executeRawJsonbAsync(dbContext, assignments, scopeFilter, cancellationToken)
        : _executeNativeAsync(dbContext, spec, scopeFilter, cancellationToken),
      maxAttempts: 5,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Native EF Core path: nested <c>SetProperty(r =&gt; r.Data.Prop, value)</c> against a
  /// <c>ComplexProperty().ToJson()</c> mapping, executed as one <c>ExecuteUpdateAsync</c>.
  /// </summary>
  private static Task<int> _executeNativeAsync(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      CancellationToken cancellationToken) {
    var (query, setters) = BuildCall(dbContext, spec, scopeFilter);
    return query.ExecuteUpdateAsync(setters.Compile(), cancellationToken);
  }

  /// <summary>
  /// True when Data is mapped as an EF Core <c>ComplexProperty().ToJson()</c> complex type — the only shape
  /// where native nested <c>SetProperty(r =&gt; r.Data.Prop, …)</c> works. False for a scalar/polymorphic jsonb
  /// column mapping (no complex sub-property to set), which forces the raw jsonb_set path.
  /// </summary>
  private static bool _dataIsComplexProperty(DbContext dbContext) {
    var entityType = dbContext.Model.FindEntityType(typeof(PerspectiveRow<TModel>));
    return entityType?.FindComplexProperty(nameof(PerspectiveRow<TModel>.Data)) is not null;
  }

  /// <summary>
  /// Null-valued-setter path: materialize the matching ids via EF (so the WHERE — including cross-perspective
  /// EXISTS — is translated by EF), then issue one raw <c>UPDATE … SET data = jsonb_set(…) WHERE id = ANY(@ids)</c>.
  /// jsonb_set with a <c>'null'::jsonb</c> value sets the sub-property to JSON null, which EF's ExecuteUpdate
  /// can't express for a complex-JSON column.
  /// </summary>
  private static async Task<int> _executeRawJsonbAsync(
      DbContext dbContext,
      IReadOnlyList<CollectiveSettersRewriter.CollectiveSetterAssignment> assignments,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      CancellationToken cancellationToken) {

    var ids = await dbContext.Set<PerspectiveRow<TModel>>()
      .Where(scopeFilter)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken)
      .ConfigureAwait(false);
    if (ids.Count == 0) {
      return 0;
    }

    var entityType = dbContext.Model.FindEntityType(typeof(PerspectiveRow<TModel>))
      ?? throw new InvalidOperationException(
        $"PerspectiveRow<{typeof(TModel).Name}> is not mapped in the DbContext model.");
    var table = entityType.GetTableName()
      ?? throw new InvalidOperationException($"PerspectiveRow<{typeof(TModel).Name}> has no table name.");
    var schema = entityType.GetSchema();
    var qualifiedTable = schema is null
      ? "\"" + table + "\""
      : "\"" + schema + "\".\"" + table + "\"";

    // Build nested jsonb_set: jsonb_set(jsonb_set(data, @path0, @p0::jsonb), @path1, @p1::jsonb).
    // The path is bound as a text[] parameter (not a '{...}' literal) so the SQL carries no braces — EF's
    // ExecuteSqlRaw would otherwise try to parse '{Prop}' as a {n} placeholder — and so the property name is
    // parameterized rather than concatenated.
    var setExpr = new StringBuilder("data");
    var parameters = new List<NpgsqlParameter>((assignments.Count * 2) + 1);
    for (var i = 0; i < assignments.Count; i++) {
      var a = assignments[i];
      var idx = i.ToString(CultureInfo.InvariantCulture);
      setExpr.Insert(0, "jsonb_set(")
        .Append(", @path").Append(idx).Append(", @p").Append(idx).Append("::jsonb)");
      parameters.Add(new NpgsqlParameter("path" + idx, new[] { a.PathName }));  // text[] path
      parameters.Add(new NpgsqlParameter("p" + idx, a.JsonValue));              // JSON text, cast ::jsonb
    }
    parameters.Add(new NpgsqlParameter("ids", ids.ToArray()));

    var sql = "UPDATE " + qualifiedTable + " SET data = " + setExpr + " WHERE id = ANY(@ids)";
    return await dbContext.Database
      .ExecuteSqlRawAsync(sql, parameters, cancellationToken)
      .ConfigureAwait(false);
  }
}
