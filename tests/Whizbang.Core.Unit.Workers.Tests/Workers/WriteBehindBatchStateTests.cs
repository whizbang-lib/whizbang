// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The state a write-behind batch prepares with (#2247): a held model answers its pair's row read, is never known to be
/// new, and every other question goes to the state source's batch, or reads per stream when there is none.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public class WriteBehindBatchStateTests {
  private const string BEHIND = "Tests.Counter";
  private const string OTHER = "Tests.Orders";

  public sealed class Model {
    public int Count { get; set; }
  }

  private sealed class HeldModelRow(Model model) : PerspectiveWriteBehindRow(PerspectiveApplyRead.Unchecked, Guid.CreateVersion7(), 1) {
    public override PerspectiveStoredRow<TModel>? ReadAs<TModel>() =>
      new PerspectiveStoredRow<Model>(ApplyRead, model) as PerspectiveStoredRow<TModel>;
  }

  private sealed class Inner : IPerspectiveBatchState {
    public PerspectiveStoredRow<Model> Row { get; } = new(PerspectiveApplyRead.Unchecked, new Model { Count = 1 });
    public bool IsKnownNew(Guid streamId, string perspectiveName) => true;
    public Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) where TModel : class =>
      Task.FromResult(Row as PerspectiveStoredRow<TModel>);
    public Task<bool?> IsPurgedAsync(IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<bool?>(true);
    public Task<bool?> HasHistoryBeforeAsync(IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult<bool?>(false);
    public Task<PerspectiveCommitSequence?> CommitSequenceOfAsync(IEventStore eventStore, Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCommitSequence?>(new PerspectiveCommitSequence(42));
  }

  private static (WriteBehindBatchState State, List<(string Perspective, bool Hit)> Lookups) _state(
      IPerspectiveBatchState? inner, Guid heldStream, Model? held) {
    var lookups = new List<(string, bool)>();
    var state = new WriteBehindBatchState(
      inner,
      (stream, name) => held is not null && stream == heldStream && name == BEHIND ? new HeldModelRow(held) : null,
      name => name == BEHIND,
      (name, hit) => lookups.Add((name, hit)));
    return (state, lookups);
  }

  [Test]
  public async Task AHeldModel_AnswersItsRow_AndIsNeverKnownNewAsync() {
    var stream = Guid.CreateVersion7();
    var held = new Model { Count = 7 };
    var (state, lookups) = _state(new Inner(), stream, held);

    var row = await state.ReadRowAsync<Model>(null!, stream, BEHIND);

    await Assert.That(row!.Model).IsSameReferenceAs(held);
    await Assert.That(await state.ReadRowAsync<object>(null!, stream, BEHIND)).IsNull()
      .Because("a held model of another type answers nothing, and the inner batch has no row of that type either");
    await Assert.That(state.IsKnownNew(stream, BEHIND)).IsFalse().Because("the held writes created the row");
    await Assert.That(state.KeepsModels(stream, BEHIND)).IsTrue();
    await Assert.That(state.KeepsModels(stream, OTHER)).IsFalse();
    await Assert.That(lookups).IsEquivalentTo([(BEHIND, true), (BEHIND, true)]);
  }

  [Test]
  public async Task WithoutAHeldModel_TheInnerBatchAnswersAsync() {
    var inner = new Inner();
    var (state, lookups) = _state(inner, Guid.CreateVersion7(), null);
    var stream = Guid.CreateVersion7();

    await Assert.That(await state.ReadRowAsync<Model>(null!, stream, BEHIND)).IsSameReferenceAs(inner.Row);
    await Assert.That(await state.ReadRowAsync<Model>(null!, stream, OTHER)).IsSameReferenceAs(inner.Row);
    await Assert.That(state.IsKnownNew(stream, BEHIND)).IsTrue();
    await Assert.That(await state.IsPurgedAsync(null!, stream, BEHIND)).IsTrue();
    await Assert.That(await state.HasHistoryBeforeAsync(null!, stream, BEHIND, Guid.CreateVersion7(), [])).IsFalse();
    await Assert.That((await state.CommitSequenceOfAsync(null!, Guid.CreateVersion7()))!.Value.Value).IsEqualTo(42);
    await Assert.That(lookups).IsEquivalentTo([(BEHIND, false)])
      .Because("only a write-behind perspective's reads count toward the hit rate");
  }

  [Test]
  public async Task WithoutAStateSource_EverythingIsReadPerStreamAsync() {
    var (state, _) = _state(null, Guid.CreateVersion7(), null);
    var stream = Guid.CreateVersion7();

    await Assert.That(await state.ReadRowAsync<Model>(null!, stream, BEHIND)).IsNull();
    await Assert.That(state.IsKnownNew(stream, BEHIND)).IsFalse();
    await Assert.That(await state.IsPurgedAsync(null!, stream, BEHIND)).IsNull();
    await Assert.That(await state.HasHistoryBeforeAsync(null!, stream, BEHIND, Guid.CreateVersion7(), [])).IsNull();
    await Assert.That(await state.CommitSequenceOfAsync(null!, Guid.CreateVersion7())).IsNull();
  }
}
