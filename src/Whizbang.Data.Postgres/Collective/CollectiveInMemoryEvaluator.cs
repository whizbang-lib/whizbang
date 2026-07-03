using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// In-memory twin of the collective SQL apply path. During a perspective replay/rebuild a stream is folded
/// row-by-row in memory (see the generated perspective runner), so a collective event that mutated that row
/// must be re-applied against the single in-memory model — not as a set-based SQL UPDATE. This evaluator does
/// exactly that: it evaluates the spec's <see cref="ICollectiveSpec{TModel}.Where"/> against the one row and,
/// when it matches, applies the spec's <see cref="ICollectiveSpec{TModel}.Setters"/> to the model instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parity with SQL.</strong> The live path compiles the same <see cref="ICollectiveSpec{TModel}"/> to a
/// <c>jsonb_set</c> UPDATE; this path compiles it to in-memory mutations. Both read the row's <em>pre-apply</em>
/// state for computed values (SQL because every <c>jsonb_set</c> in one statement references the original
/// <c>data</c>; this evaluator because writes are deferred until every setter's value has been computed).
/// </para>
/// <para>
/// <strong>Why self-referential only.</strong> A single-stream fold has just the one row — no sibling
/// perspective rows — so a spec whose <c>Where</c> queries <see cref="ICollectiveQuery"/> can't be evaluated
/// here. That is prohibited at compile time by <c>WHIZ106</c> (the collective-apply purity analyzer), so every
/// spec that reaches this evaluator depends only on the row's own state and the event's static payload.
/// </para>
/// <para>
/// <strong>AOT.</strong> Uses <see cref="Expression{TDelegate}.Compile()"/> and reflection
/// <see cref="PropertyInfo.SetValue(object, object)"/> — the same reflection tradeoff the driver SQL compilers
/// already accept (suppressed below). Replay/rebuild is an operational path, not the AOT-published hot path.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Collective in-memory replay accepts the same reflection tradeoff as the driver SQL compilers; used on the rebuild/replay path, not the AOT-published hot path.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Collective in-memory replay compiles the spec's expression trees, matching the existing collective SQL-compiler tradeoff; rebuild/replay path only.")]
[SuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern", Justification = "Property is recovered from the selector's compile-time MemberExpression metadata, not a string lookup.")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the Whizbang collective compiler pattern of generic-over-TModel static helpers (DapperCollectiveSpecCompiler, EFCoreCollectiveAdapter).")]
public static class CollectiveInMemoryEvaluator<TModel> where TModel : class {
  /// <summary>
  /// Whether the single row identified by <paramref name="id"/> with data <paramref name="current"/> is in the
  /// spec's cohort. A null <c>Where</c> means "no per-row refinement" (tenant scope is enforced upstream by only
  /// folding in-scope collective events), so it matches.
  /// </summary>
  public static bool Matches(ICollectiveSpec<TModel> spec, Guid id, TModel current) {
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(current);

    if (spec.Where is null) {
      return true;
    }

    var predicate = spec.Where.Compile();
    return predicate(_row(id, current));
  }

  /// <summary>
  /// Applies the spec's setters to <paramref name="current"/> in place and returns it. Computed setters are
  /// evaluated against the pre-apply state (writes are deferred until all values are computed), matching the
  /// SQL path where every <c>jsonb_set</c> reads the original <c>data</c>.
  /// </summary>
  public static TModel Apply(ICollectiveSpec<TModel> spec, TModel current) {
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(current);

    var setters = new _inMemorySetters(current);
    spec.Setters.Compile().Invoke(setters);
    setters.Flush();
    return current;
  }

  // A PerspectiveRow wrapping the single in-memory model. Only Id/Data are meaningful to a self-referential
  // Where; the remaining required members are filled with defaults (they never appear in a replay-safe predicate).
  private static PerspectiveRow<TModel> _row(Guid id, TModel data) => new() {
    Id = id,
    Data = data,
    Metadata = new PerspectiveMetadata(),
    Scope = new PerspectiveScope(),
    CreatedAt = default,
    UpdatedAt = default,
    Version = 0,
  };

  /// <summary>
  /// In-memory realization of <see cref="ICollectiveSetters{TModel}"/>. Each <c>SetProperty</c> resolves the
  /// value <em>now</em> (constant, or computed against the pre-apply model) and defers the actual write to
  /// <see cref="Flush"/>, so every computed value reads the original state — matching the SQL path.
  /// </summary>
  private sealed class _inMemorySetters(TModel original) : ICollectiveSetters<TModel> {
    private readonly List<(PropertyInfo Property, object? Value)> _writes = new();

    public ICollectiveSetters<TModel> SetProperty<TProp>(
        Expression<Func<TModel, TProp>> selector, TProp value) {
      _writes.Add((_property(selector), value));
      return this;
    }

    public ICollectiveSetters<TModel> SetProperty<TProp>(
        Expression<Func<TModel, TProp>> selector, Expression<Func<TModel, TProp>> computed) {
      var value = computed.Compile().Invoke(original);
      _writes.Add((_property(selector), value));
      return this;
    }

    public void Flush() {
      foreach (var (property, value) in _writes) {
        property.SetValue(original, value);
      }
    }

    private static PropertyInfo _property<TProp>(Expression<Func<TModel, TProp>> selector) {
      var body = selector.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        body = convert.Operand;
      }
      if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }) {
        return prop;
      }
      throw new NotSupportedException(
        $"CollectiveInMemoryEvaluator<{typeof(TModel).Name}> only supports scalar top-level property selectors " +
        "(o => o.PropertyName). Nested paths, indexed access, or computed selectors are not replayable in-memory.");
    }
  }
}
