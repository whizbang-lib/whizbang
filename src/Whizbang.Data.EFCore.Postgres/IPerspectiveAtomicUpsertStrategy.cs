using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Slice 22a (plans/slice-22-source-gen-atomic-upsert.md) — non-generic surface over the
/// source-generated typed atomic-upsert strategies. The Whizbang perspective generator
/// emits a strongly-typed <c>GeneratedAtomicUpsertStrategy&lt;TModel&gt;</c> per discovered
/// perspective; instances are registered in
/// <see cref="PerspectiveAtomicUpsertRegistry"/> via <c>[ModuleInitializer]</c> and
/// dispatched here when <see cref="BaseUpsertStrategy"/> hits the fast path.
/// </summary>
/// <remarks>
/// <para>
/// Box-once at the registry boundary: the typed strategy unboxes the model arg inside
/// <see cref="UpsertAsync"/>. Avoids a per-call generic constructor on the dispatcher.
/// </para>
/// <para>
/// Strategies issue a single <c>INSERT ... ON CONFLICT (id) DO UPDATE</c> via
/// <c>ExecuteSqlRawAsync</c>. JSONB columns are serialized with source-generated
/// <c>JsonTypeInfo</c> from <c>PerspectiveJsonContext</c> (slice 22b) so the byte format
/// matches EF Core 10's <c>ComplexProperty().ToJson()</c> reader exactly.
/// </para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAtomicUpsertRegistryTests.cs</tests>
public interface IPerspectiveAtomicUpsertStrategy {
  /// <summary>
  /// Persist a perspective row atomically. The implementation runs one
  /// <c>INSERT ... ON CONFLICT (id) DO UPDATE</c> statement; no
  /// <c>SELECT-then-INSERT/UPDATE</c> race window.
  /// </summary>
  /// <param name="context">DbContext used for the connection + execution-strategy.</param>
  /// <param name="id">Row id (perspective primary key).</param>
  /// <param name="model">Boxed <c>TModel</c> instance; the strategy unboxes inside.</param>
  /// <param name="metadata">Per-event metadata (cloned by the caller).</param>
  /// <param name="scope">Tenant/user/principal scope (cloned by the caller).</param>
  /// <param name="forceUpdateScope">When <c>true</c>, the conflict path also refreshes <c>scope</c> (<c>IScopeEvent</c> semantics). When <c>false</c>, the existing row's scope is preserved.</param>
  /// <param name="cancellationToken">Cancellation propagation.</param>
  Task UpsertAsync(
      DbContext context,
      Guid id,
      object model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default);
}
