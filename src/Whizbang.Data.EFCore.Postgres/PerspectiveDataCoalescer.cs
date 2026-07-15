using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Coalesces null nested collections in a materialized <c>PerspectiveRow&lt;TModel&gt;.Data</c> model to empty,
/// working around an EF Core 10 defect: <c>ComplexProperty().ToJson()</c> materializes a JSON-absent complex
/// collection as <see langword="null"/> (it does NOT run the CLR field initializer <c>= []</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> When a nested collection field is added to a read model (schema evolution),
/// every row written before the change ("old-shape") has stored JSON that lacks the new key. On read, EF Core 10
/// materializes that field as <see langword="null"/> instead of an empty list, so the row is
/// <em>poison-on-touch</em>: any consumer that reads the collection throws <see cref="NullReferenceException"/>,
/// and saving the tracked entity throws <see cref="InvalidOperationException"/> in
/// <c>PrepareToSave.CheckForNullComplexProperties</c> ("complex type property … required … but has a null value
/// when saving changes"). This reproduces through at least EF Core 10.0.10; filed upstream as
/// <see href="https://github.com/dotnet/efcore/issues/38625">dotnet/efcore#38625</see> — remove this workaround
/// once a fixed EF Core release ships. Whizbang's upsert save path already sidesteps the SAVE
/// crash (it never load-mutates the tracked graph); this closes the remaining READ/APPLY hazard.
/// </para>
/// <para>
/// <strong>Zero reflection, AOT-safe.</strong> Per-model coalescers are registered by generated code, keyed by
/// the closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>. The runtime lookup is
/// <c>entity.GetType()</c> (a CLR vtable intrinsic) plus a dictionary hash — no <c>IsGenericType</c>,
/// no <c>GetGenericArguments()</c>, no property-graph reflection. Mirrors
/// <see cref="SplitModeChangeTrackerHydrator"/>.
/// </para>
/// <para>
/// <strong>Two entry points.</strong> <see cref="EnsureHooked"/> subscribes to <see cref="ChangeTracker.Tracked"/>
/// so tracked reads coalesce automatically; <see cref="Coalesce"/> coalesces a single materialized instance for
/// no-tracking reads (and the upsert's existing-row read). Unlike the split-mode hydrator this does NOT detach
/// the entity — a coalesced collection must persist if the row is subsequently saved.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkflowSchemaEvolutionComplexTypeTests.cs</tests>
public static class PerspectiveDataCoalescer {
  /// <summary>
  /// Coalescers keyed by closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>. Each delegate walks the
  /// entity's <c>Data</c> graph and replaces null nested collections with empty instances.
  /// </summary>
  private static readonly ConcurrentDictionary<Type, Action<object>> _coalescers = new();

  /// <summary>
  /// DbContext instances whose <see cref="ChangeTracker.Tracked"/> handler is already subscribed. Uses
  /// <see cref="ConditionalWeakTable{TKey,TValue}"/> so entries are collected with the DbContext.
  /// </summary>
  private static readonly ConditionalWeakTable<DbContext, object> _hooked = [];

  private static readonly object _sentinel = new();

  /// <summary>
  /// Registers a coalescer for a perspective model. Called by generated code at startup.
  /// </summary>
  /// <param name="perspectiveRowType">The closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>.</param>
  /// <param name="coalescer">Delegate that null-coalesces the nested collections in the entity's Data model.</param>
  public static void Register(Type perspectiveRowType, Action<object> coalescer) {
    ArgumentNullException.ThrowIfNull(perspectiveRowType);
    ArgumentNullException.ThrowIfNull(coalescer);
    _coalescers[perspectiveRowType] = coalescer;
  }

  /// <summary>
  /// Whether a coalescer is registered for the given closed generic type.
  /// </summary>
  public static bool HasCoalescer(Type perspectiveRowType) => _coalescers.ContainsKey(perspectiveRowType);

  /// <summary>
  /// Coalesces the null nested collections of a single materialized entity, if a coalescer is registered for its
  /// runtime type. Safe to call on any object; a no-op when nothing is registered. Use this on no-tracking reads.
  /// </summary>
  public static void Coalesce(object entity) {
    ArgumentNullException.ThrowIfNull(entity);
    if (_coalescers.TryGetValue(entity.GetType(), out var coalescer)) {
      coalescer(entity);
    }
  }

  /// <summary>
  /// Ensures the <see cref="ChangeTracker.Tracked"/> handler is subscribed on the given context. Idempotent —
  /// subscribes at most once per instance.
  /// </summary>
  public static void EnsureHooked(DbContext context) {
    ArgumentNullException.ThrowIfNull(context);
    if (!_hooked.TryAdd(context, _sentinel)) {
      return;
    }
    context.ChangeTracker.Tracked += _onEntityTracked;
  }

  /// <summary>Clears all registered coalescers. Test-only.</summary>
  internal static void Clear() => _coalescers.Clear();

  private static void _onEntityTracked(object? sender, EntityTrackedEventArgs args) => Coalesce(args.Entry.Entity);
}
