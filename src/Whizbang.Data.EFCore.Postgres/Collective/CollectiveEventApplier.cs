using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// End-to-end coordinator for applying a single
/// <see cref="ICollectiveEvent"/> against an EF Core-backed perspective.
/// Composes the four pieces lined up by Slices 1–6 into one call:
/// </summary>
/// <list type="number">
///   <item><description>Validates the entry from <c>CollectiveApplyRegistry</c> matches the event.</description></item>
///   <item><description>Validates the resolver matches the event's <see cref="ICollectiveScope.ScopeKind"/>.</description></item>
///   <item><description>Enters the scope's ambient <c>ScopeContextAccessor</c> (re-entrance safe).</description></item>
///   <item><description>Invokes the perspective handler via the entry's static <see cref="CollectiveApplyEntry.Invoker"/> delegate.</description></item>
///   <item><description>Hands the resulting <see cref="ICollectiveSpec{TModel}"/> to <see cref="EFCoreCollectiveAdapter{TModel}.ExecuteAsync"/> with the resolver's <c>ScopeFilter</c>, the event's <see cref="ICollectiveEvent.MatchedStreamIds"/>, and the event's id.</description></item>
/// </list>
/// <remarks>
/// <para>
/// Generic over <typeparamref name="TModel"/> so callers compose with the
/// type they know at compile time. Production wiring into
/// <c>PerspectiveWorker</c> (or an equivalent host) is the next slice —
/// that's where the type-erased dispatch (look up entry by
/// <see cref="ICollectiveEvent"/> runtime type, fan out to the right
/// generic instance of this class) lives.
/// </para>
/// <para>
/// Validation failures throw <see cref="ArgumentException"/> at the
/// site rather than silently no-op'ing: a wrong entry / wrong resolver
/// indicates a misconfigured registry or DI dispatch, not a domain
/// concern, and silently dropping the apply would corrupt the
/// projection state without telling anyone.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the established Whizbang.Data.EFCore.Postgres pattern for generic-over-TModel static helpers (e.g. EFCoreCollectiveAdapter<TModel> from Slice 6).")]
public sealed class CollectiveEventApplier<TModel> where TModel : class {
  /// <summary>
  /// Apply a collective event against the EF-backed perspective.
  /// </summary>
  /// <param name="entry">Compile-time entry from <c>CollectiveApplyRegistry</c> describing the handler.</param>
  /// <param name="handlerInstance">DI-resolved instance of the entry's <c>HandlerType</c>. Passed as <see cref="object"/> because the entry's <see cref="CollectiveApplyEntry.Invoker"/> is type-erased.</param>
  /// <param name="evt">The collective event being applied.</param>
  /// <param name="resolver">DI-resolved <see cref="ICollectiveScopeResolver"/> whose <see cref="ICollectiveScopeResolver.ScopeKind"/> matches <paramref name="evt"/>.</param>
  /// <param name="dbContext">EF Core context that holds the perspective table.</param>
  /// <param name="collectiveEventId">Unique id of this collective event; emitted in the apply-completion telemetry (which event mutated how many rows).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows the SQL UPDATE mutated.</returns>
  public static async Task<int> ApplyAsync(
      CollectiveApplyEntry entry,
      object handlerInstance,
      ICollectiveEvent evt,
      ICollectiveScopeResolver resolver,
      DbContext dbContext,
      Guid collectiveEventId,
      CollectiveApplyOptions options,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(handlerInstance);
    ArgumentNullException.ThrowIfNull(evt);
    ArgumentNullException.ThrowIfNull(resolver);
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(options);

    // Validate: the entry must match the event's concrete type and the
    // model we're generic over.
    if (entry.EventType != evt.GetType()) {
      throw new ArgumentException(
        $"Entry's EventType {entry.EventType.FullName} does not match the supplied event type {evt.GetType().FullName}. Registry lookup or dispatch routing is wrong.",
        nameof(entry));
    }
    if (entry.ModelType != typeof(TModel)) {
      throw new ArgumentException(
        $"Entry's ModelType {entry.ModelType.FullName} does not match TModel {typeof(TModel).FullName}. The dispatcher should fan out to CollectiveEventApplier<{entry.ModelType.Name}> instead.",
        nameof(entry));
    }

    // Validate: the resolver must handle the event's scope kind.
    if (resolver.ScopeKind != evt.Scope.ScopeKind) {
      throw new ArgumentException(
        $"Resolver.ScopeKind '{resolver.ScopeKind}' does not match the event's Scope.ScopeKind '{evt.Scope.ScopeKind}'. The DI dispatch should have routed to a different resolver.",
        nameof(resolver));
    }

    // Enter the resolver's ambient scope context for the duration of
    // Apply + adapter execution. Disposing restores prior context
    // regardless of whether the body threw.
    using var _ = resolver.EnterContext(evt.Scope);

    // Invoke the handler. The entry's Invoker is the AOT-clean static
    // lambda the source generator (Slice 5) emitted — typed casts only,
    // no reflection. The query context lets the handler's Where reference
    // sibling perspectives (translated to a correlated EXISTS by EF).
    var query = new EFCoreCollectiveQuery(dbContext);
    if (entry.Invoker(handlerInstance, evt, query) is not ICollectiveSpec<TModel> spec) {
      throw new InvalidOperationException(
        $"Handler {entry.HandlerType.FullName}.{entry.MethodName} returned null or a non-{nameof(ICollectiveSpec<TModel>)}<{typeof(TModel).Name}> instance. The generator's Invoker shape is broken or the handler is misconfigured.");
    }

    // Compose the effective WHERE. The resolver's scope envelope is ALWAYS computed and always binds (D0
    // safety on shared multi-tenant tables): Framework AND-composes it with the optional handler Where;
    // Custom AND-composes it with the mandatory handler cohort Where. A handler can refine within scope but
    // never escape it.
    var scopeFilter = resolver.ScopeFilter<TModel>(evt.Scope);
    var effectiveWhere = CollectiveWhereComposer.Compose(entry.ScopeHandling, scopeFilter, spec.Where);

    // A stable identity for the scope (includes the tenant), so the per-(table,scope) advisory lock serializes
    // same-scope applies while letting disjoint scopes run concurrently. The record ToString() is
    // compiler-generated (AOT-safe, no reflection) and carries the scope's members (e.g. TenantId).
    var scopeKey = evt.Scope.ScopeKind + ":" + evt.Scope.ToString();

    // §6: fold this handler's per-apply knob overrides onto the global default (0 = inherit). SerializeApplies
    // and EnsureIndexes stay global — exclusive serialization is not per-handler optional (D4 safety).
    var effectiveOptions = options with {
      BatchSize = entry.BatchSizeOverride > 0 ? entry.BatchSizeOverride : options.BatchSize,
      StatementTimeoutSeconds = entry.StatementTimeoutSecondsOverride > 0
        ? entry.StatementTimeoutSecondsOverride
        : options.StatementTimeoutSeconds,
    };

    return await EFCoreCollectiveAdapter<TModel>.ExecuteAsync(
      dbContext,
      spec,
      effectiveWhere,
      effectiveOptions,
      scopeKey,
      collectiveEventId,
      cancellationToken).ConfigureAwait(false);
  }
}
