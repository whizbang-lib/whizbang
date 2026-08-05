using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A single entry in the compile-time dispatch table emitted by
/// <c>CollectiveApplyDiscoveryGenerator</c> (Slice 5). The projection
/// runner (Slice 7) iterates the registry to find the handler for an
/// incoming <see cref="ICollectiveEvent"/> and invokes it via
/// <see cref="Invoker"/> — no reflection, AOT-clean.
/// </summary>
/// <param name="ModelType">
/// The perspective's <c>TModel</c> (the rows the handler mutates).
/// Used to match a collective event against the projection table.
/// </param>
/// <param name="EventType">
/// The concrete <see cref="ICollectiveEvent"/> subtype this entry
/// responds to.
/// </param>
/// <param name="HandlerType">
/// The class declaring the <c>[CollectiveApplyFor]</c>-attributed method.
/// Resolved from DI at apply time and cast to <see cref="object"/>
/// before being handed to <see cref="Invoker"/>.
/// </param>
/// <param name="MethodName">
/// The handler method name (useful for diagnostics and source-link
/// tooling; the runtime invokes via <see cref="Invoker"/> instead of
/// reflecting on the name).
/// </param>
/// <param name="ScopeHandling">
/// How the framework composes the SQL UPDATE's WHERE clause for this
/// handler — see <see cref="CollectiveScopeHandling"/>.
/// </param>
/// <param name="SpecKind">
/// Which adapter pipeline executes the returned spec — see
/// <see cref="CollectiveSpecKind"/>.
/// </param>
/// <param name="Invoker">
/// Type-erased delegate that calls the handler's <c>Apply</c> method
/// with the right cast on both <paramref name="HandlerType"/> and the
/// event, threading the driver-bound <see cref="ICollectiveQuery"/> so the
/// handler's <c>Where</c> can reference sibling perspectives. The generator
/// emits a static lambda per entry — no reflection, AOT-clean by
/// construction. Handlers that don't need the query context simply ignore
/// the third argument.
/// </param>
/// <param name="BatchSizeOverride">
/// Per-handler override for <see cref="CollectiveApplyOptions.BatchSize"/>; <c>0</c> inherits the global
/// default. Emitted from <c>[CollectiveApplyFor(BatchSize = …)]</c>.
/// </param>
/// <param name="StatementTimeoutSecondsOverride">
/// Per-handler override for <see cref="CollectiveApplyOptions.StatementTimeoutSeconds"/>; <c>0</c> inherits the
/// global default. Emitted from <c>[CollectiveApplyFor(StatementTimeoutSeconds = …)]</c>.
/// </param>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_OneEntry_InvokesExecutorOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_TwoEntriesSameEventDifferentModels_FansOutAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveEventApplierTests.cs:ApplyAsync_EventTypeMismatch_ThrowsArgumentExceptionAsync</tests>
public sealed record CollectiveApplyEntry(
  Type ModelType,
  Type EventType,
  Type HandlerType,
  string MethodName,
  CollectiveScopeHandling ScopeHandling,
  CollectiveSpecKind SpecKind,
  Func<object, ICollectiveEvent, ICollectiveQuery, object> Invoker,
  int BatchSizeOverride = 0,
  int StatementTimeoutSecondsOverride = 0
);
