// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// One fan-out a perspective declares (<see cref="IPerspectiveFanOutFor{TModel, TEvent}"/>): the rows of
/// <see cref="ModelType"/> whose <see cref="KeyColumn"/> holds an <see cref="EventType"/>'s stream id, and the
/// set-based change <see cref="Invoker"/> returns for them. Emitted by the generator, one per (model, event), into the
/// assembly's <see cref="IPerspectiveFanOutRegistry"/>.
/// </summary>
/// <param name="PerspectiveType">The perspective that declares the fan-out, resolved from the container at apply time.</param>
/// <param name="ModelType">The perspective's model: the rows the event targets.</param>
/// <param name="EventType">The other stream's event.</param>
/// <param name="KeyProperty">The model's key property, as <see cref="FanOut.By{TModel}"/> names it.</param>
/// <param name="KeyColumn">The key property's column: its <c>[PhysicalField(ColumnName)]</c>, else its snake_case name.</param>
/// <param name="Invoker">
/// Calls the perspective's <c>Apply</c> for the event, with both casts made statically by the generated lambda (no
/// reflection), and returns the setters.
/// </param>
/// <param name="KeySelector">
/// The key property as an expression over the model, <c>row =&gt; row.Key</c>, as <see cref="FanOut.By{TModel}"/> declares
/// it (an <c>Expression&lt;Func&lt;TModel, object?&gt;&gt;</c>, emitted by the generator). The live apply matches rows by
/// it (<see cref="FanOutScopeResolver"/>), with no member looked up by name.
/// </param>
/// <param name="TableName">The model's perspective table, as the schema names it; the generator emits it.</param>
/// <param name="CreateExecutor">
/// Makes the driver's executor for the model through the driver's <see cref="ICollectiveExecutorFactory"/>
/// (<c>factory =&gt; factory.Create&lt;TModel&gt;(table)</c>, emitted by the generator), so the dispatcher gives every
/// fan-out model its executor with no registration to remember. Null only for an entry written by hand, which then
/// needs the model's executor registered; the startup validator refuses a host where it is not.
/// </param>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:Entry_Invoker_CallsThePerspectivesSetForTheEventAsync</tests>
public sealed record PerspectiveFanOutEntry(
  Type PerspectiveType,
  Type ModelType,
  Type EventType,
  string KeyProperty,
  string KeyColumn,
  Func<object, IEvent, ICollectiveSpecSetters> Invoker,
  LambdaExpression KeySelector,
  string TableName = "",
  Func<ICollectiveExecutorFactory, ICollectiveEventExecutor>? CreateExecutor = null
) {
  /// <summary>
  /// The stable identity of this fan-out: the perspective, its <c>Apply</c> for the event, and the model. Built from names,
  /// so it survives a restart and a redeploy, as <see cref="CollectiveApplyEntry.EntryKey"/> does.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:Entry_EntryKey_NamesThePerspectiveTheEventAndTheModelAsync</tests>
  public string EntryKey =>
    TypeNameFormatter.FormatClrTypeName(PerspectiveType) + ".Apply(" + TypeNameFormatter.FormatClrTypeName(EventType) + "):"
    + TypeNameFormatter.FormatClrTypeName(ModelType);

  /// <summary>
  /// The collective event a foreign event is dispatched as (#2620): the event with its stream id and tenant, which
  /// <see cref="ICollectiveDispatcher"/> applies through every fan-out of the event's type, each scoped by
  /// <see cref="FanOutScopeResolver"/> to the rows whose key holds <paramref name="streamId"/> in
  /// <paramref name="tenantId"/>. The collective sink dispatches each fan-out row this way.
  /// </summary>
  /// <param name="foreignEvent">The foreign event, as stored.</param>
  /// <param name="streamId">The event's stream id: the key value of the rows it targets.</param>
  /// <param name="tenantId">The event's tenant.</param>
  /// <returns>The event to hand to <see cref="ICollectiveDispatcher.DispatchAsync"/>.</returns>
  /// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:FanOutCollectiveEvent_ItsScope_IsTheStreamAndTenant_IdentifiedByBothAsync</tests>
  public static ICollectiveEvent CollectiveEventFor(IEvent foreignEvent, Guid streamId, string tenantId) =>
    new FanOutCollectiveEvent(foreignEvent, streamId, tenantId);

  /// <summary>
  /// This fan-out as the collective machinery applies it (#2620): an entry for the perspective's <c>Apply</c> of
  /// <see cref="EventType"/> on <see cref="ModelType"/>, under <see cref="EntryKey"/>, that applies only to a
  /// <see cref="FanOutCollectiveEvent"/> of the event and calls <c>Apply</c> with the foreign event it carries. The scope
  /// is the framework's: <see cref="FanOutScopeResolver"/>'s key-and-tenant filter always binds.
  /// </summary>
  /// <returns>The entry the collective dispatcher runs.</returns>
  /// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ToCollectiveApplyEntry_KeepsTheFanOutsIdentity_SoItsProgressIsItsOwnAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ToCollectiveApplyEntry_TheInvoker_CallsApplyWithTheForeignEventAsync</tests>
  public CollectiveApplyEntry ToCollectiveApplyEntry() {
    var invoker = Invoker;
    return new CollectiveApplyEntry(
      ModelType, EventType, PerspectiveType, "Apply(" + TypeNameFormatter.FormatClrTypeName(EventType) + ")",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq,
      (perspective, evt, _) => invoker(perspective, ((FanOutCollectiveEvent)evt).Event)) {
      FanOut = this,
    };
  }
}
