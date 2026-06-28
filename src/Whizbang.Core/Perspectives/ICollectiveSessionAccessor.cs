using System;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Driver-agnostic seam that hands the <c>PerspectiveWorker</c> the database session
/// <see cref="ICollectiveDispatcher.DispatchAsync"/> needs — without the worker referencing any
/// driver type. Each storage driver registers its own implementation:
/// EF Core returns the perspective <c>DbContext</c>; Dapper returns the <c>NpgsqlConnection</c>.
/// </summary>
/// <remarks>
/// The worker resolves this from the per-stream group scope and forwards the returned session as
/// <c>object</c> to the dispatcher, which forwards it to each <see cref="ICollectiveEventExecutor"/> —
/// the executor casts to its expected driver type (Q1 of the consume-wiring design).
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public interface ICollectiveSessionAccessor {
  /// <summary>
  /// Resolve the driver-specific projection session (EF <c>DbContext</c> / Dapper
  /// <c>NpgsqlConnection</c>) from the supplied scoped service provider. The returned object is passed
  /// straight to <see cref="ICollectiveDispatcher.DispatchAsync"/>.
  /// </summary>
  object GetSession(IServiceProvider scopedServiceProvider);
}
