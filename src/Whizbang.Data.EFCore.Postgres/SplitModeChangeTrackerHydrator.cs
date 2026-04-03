using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hydrates Split-mode physical field values into the <c>Data</c> model after EF Core
/// materializes a <c>PerspectiveRow&lt;TModel&gt;</c>, using the
/// <see cref="ChangeTracker.Tracked"/> event.
/// </summary>
/// <remarks>
/// <para>
/// EF Core's <c>ComplexProperty().ToJson()</c> populates the <c>Data</c> property from JSONB
/// <em>after</em> <c>IMaterializationInterceptor.InitializedInstance</c> fires, making the
/// interceptor unable to write physical field values into <c>Data</c> (it's still null).
/// </para>
/// <para>
/// The <c>ChangeTracker.Tracked</c> event fires when an entity enters the state manager,
/// which happens <em>after</em> full materialization — including complex JSON properties.
/// At this point, <c>Data</c> is populated from JSONB and shadow properties contain the
/// physical column values, so we can copy them into <c>Data</c> and immediately detach
/// the entity.
/// </para>
/// <para>
/// <strong>Zero reflection, AOT-safe</strong>: Hydrators are registered by generated code at
/// startup, keyed by the closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>.
/// The runtime lookup is <c>entity.GetType()</c> (CLR vtable intrinsic) + dictionary hash.
/// No <c>IsGenericType</c>, no <c>GetGenericTypeDefinition()</c>, no <c>GetGenericArguments()</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs</tests>
public static class SplitModeChangeTrackerHydrator {
  /// <summary>
  /// Hydrators keyed by closed generic type: <c>typeof(PerspectiveRow&lt;MyModel&gt;)</c>.
  /// Each delegate reads shadow property values from the <see cref="EntityEntry"/> and copies
  /// them into the <c>Data</c> model, then detaches the entity.
  /// </summary>
  private static readonly ConcurrentDictionary<Type, Action<EntityEntry>> _hydrators = new();

  /// <summary>
  /// Tracks which <see cref="DbContext"/> instances already have the
  /// <see cref="ChangeTracker.Tracked"/> handler subscribed.
  /// Uses <see cref="ConditionalWeakTable{TKey,TValue}"/> so entries are automatically
  /// cleaned up when the DbContext is garbage collected.
  /// </summary>
  private static readonly ConditionalWeakTable<DbContext, object> _hooked = new();

  /// <summary>
  /// Registers a hydrator for a Split-mode perspective model.
  /// Called by generated code at startup.
  /// </summary>
  /// <param name="perspectiveRowType">
  /// The closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>.
  /// Must match the runtime type returned by <c>entity.GetType()</c>.
  /// </param>
  /// <param name="hydrator">
  /// Delegate that reads shadow property values from the <see cref="EntityEntry"/>,
  /// copies them into the <c>Data</c> model, and sets
  /// <c>entry.State = EntityState.Detached</c>.
  /// </param>
  public static void Register(Type perspectiveRowType, Action<EntityEntry> hydrator) {
    ArgumentNullException.ThrowIfNull(perspectiveRowType);
    ArgumentNullException.ThrowIfNull(hydrator);
    _hydrators[perspectiveRowType] = hydrator;
  }

  /// <summary>
  /// Checks whether a hydrator is registered for the given closed generic type.
  /// Used by scoped access classes to determine whether to use tracking queries.
  /// </summary>
  /// <param name="perspectiveRowType">
  /// The closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>.
  /// </param>
  public static bool HasHydrator(Type perspectiveRowType) {
    return _hydrators.ContainsKey(perspectiveRowType);
  }

  /// <summary>
  /// Ensures the <see cref="ChangeTracker.Tracked"/> event handler is subscribed
  /// on the given <see cref="DbContext"/>. Idempotent — subscribes at most once per instance.
  /// </summary>
  /// <param name="context">The DbContext to hook into.</param>
  public static void EnsureHooked(DbContext context) {
    ArgumentNullException.ThrowIfNull(context);

    // ConditionalWeakTable.TryAdd is atomic; returns false if already present
    if (!_hooked.TryAdd(context, _sentinel)) {
      return;
    }

    context.ChangeTracker.Tracked += _onEntityTracked;
  }

  /// <summary>
  /// Clears all registered hydrators and hooks. Used for testing.
  /// </summary>
  internal static void Clear() {
    _hydrators.Clear();
  }

  /// <summary>
  /// Sentinel object used as the value in <see cref="_hooked"/>.
  /// </summary>
  private static readonly object _sentinel = new();

  /// <summary>
  /// Event handler for <see cref="ChangeTracker.Tracked"/>.
  /// Zero-reflection: performs a single dictionary lookup by <c>entity.GetType()</c>.
  /// </summary>
  private static void _onEntityTracked(object? sender, EntityTrackedEventArgs args) {
    if (_hydrators.TryGetValue(args.Entry.Entity.GetType(), out var hydrator)) {
      hydrator(args.Entry);
    }
  }
}
