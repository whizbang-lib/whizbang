// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:Entry_Invoker_CallsThePerspectivesSetForTheEventAsync</tests>
public sealed record PerspectiveFanOutEntry(
  Type PerspectiveType,
  Type ModelType,
  Type EventType,
  string KeyProperty,
  string KeyColumn,
  Func<object, IEvent, ICollectiveSpecSetters> Invoker
) {
  /// <summary>
  /// The stable identity of this fan-out: the perspective, its <c>Apply</c> for the event, and the model. Built from names,
  /// so it survives a restart and a redeploy, as <see cref="CollectiveApplyEntry.EntryKey"/> does.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:Entry_EntryKey_NamesThePerspectiveTheEventAndTheModelAsync</tests>
  public string EntryKey =>
    TypeNameFormatter.FormatClrTypeName(PerspectiveType) + ".Apply(" + TypeNameFormatter.FormatClrTypeName(EventType) + "):"
    + TypeNameFormatter.FormatClrTypeName(ModelType);
}
