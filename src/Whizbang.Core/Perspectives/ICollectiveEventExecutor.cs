using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Non-generic seam the projection runner calls to apply a collective
/// event. Each implementation closes over a concrete <c>TModel</c> at
/// construction time and advertises that type via <see cref="ModelType"/>.
/// The runner registers one executor per <c>TModel</c> in DI and looks
/// up the right one by filtering
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> on
/// <see cref="ModelType"/> — no <see cref="System.Type.MakeGenericType(System.Type[])"/>
/// at runtime, AOT-clean by construction.
/// </summary>
/// <remarks>
/// <para>
/// The <c>dbContextOrSession</c> parameter is typed as
/// <see cref="object"/> rather than a driver-specific type so the
/// interface stays in <c>Whizbang.Core</c> (which has no dependency on
/// EF Core or Dapper). Each driver's executor casts to the right
/// concrete session type at the call boundary; mismatches throw
/// <see cref="ArgumentException"/> with the parameter context attached.
/// </para>
/// <para>
/// <strong>Why a non-generic interface.</strong> The
/// <see cref="CollectiveApplyEntry.ModelType"/> on a registry entry is
/// a <see cref="Type"/> — known only at runtime when the inbox payload
/// is deserialized. Closing the generic at runtime would require
/// reflection; closing it at registration time (one executor per
/// <c>TModel</c>) folds the generic resolution into the consumer's
/// DI graph, which the source generator can drive at compile time.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveEventExecutorTests.cs:ModelType_ReportsTheClosedGenericArgumentAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveEventExecutorTests.cs:ModelType_DifferentTModel_DifferentDiscriminatorAsync</tests>
public interface ICollectiveEventExecutor {
  /// <summary>
  /// The closed <c>TModel</c> this executor handles. The dispatcher
  /// (Slice 7b) filters on this to fan out by entry's
  /// <see cref="CollectiveApplyEntry.ModelType"/>.
  /// </summary>
  Type ModelType { get; }

  /// <summary>
  /// Apply the collective event against the executor's
  /// <see cref="ModelType"/>.
  /// </summary>
  /// <param name="entry">Compile-time entry from <c>CollectiveApplyRegistry</c> describing the handler. Must have <see cref="CollectiveApplyEntry.ModelType"/> matching this executor's <see cref="ModelType"/>.</param>
  /// <param name="handlerInstance">DI-resolved instance of the entry's <c>HandlerType</c>.</param>
  /// <param name="evt">The collective event being applied.</param>
  /// <param name="resolver">DI-resolved <see cref="ICollectiveScopeResolver"/> matching <paramref name="evt"/>'s scope kind.</param>
  /// <param name="dbContextOrSession">Driver-specific session (a <c>DbContext</c> for EF Core, an <c>NpgsqlConnection</c> for Dapper). The implementation casts to the type it expects; wrong-type input throws <see cref="ArgumentException"/>.</param>
  /// <param name="collectiveEventId">Unique id of this collective event; emitted in the apply-completion telemetry.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows the SQL UPDATE mutated.</returns>
#pragma warning disable S107 // Apply-chain seam threads the full dispatch context (entry, session, hooks, per-batch callback) — a parameter object would ripple through every driver and fake for no call-site gain
  Task<int> ApplyAsync(
    CollectiveApplyEntry entry,
    object handlerInstance,
    ICollectiveEvent evt,
    ICollectiveScopeResolver resolver,
    object dbContextOrSession,
    Guid collectiveEventId,
    Func<CancellationToken, ValueTask>? onBatchApplied = null,
    CancellationToken cancellationToken = default);
#pragma warning restore S107
}
