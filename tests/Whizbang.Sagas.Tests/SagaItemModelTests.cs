using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the per-item projection row. SagaItemModel is the read-model
/// shape every per-item saga event projects into; it must implement
/// <see cref="ISagaItem"/> so generic helpers and live-progress
/// resolvers can read it without knowing the concrete type.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemModelTests {

  [Test]
  public async Task ImplementsISagaItemAsync() {
    var item = new SagaItemModel();

    await Assert.That(item is ISagaItem).IsTrue()
      .Because("SagaApplyHelper and SagaLiveProgressResolvers operate on ISagaItem; if the concrete type ever stops implementing it, generic consumers silently break.");
  }

  [Test]
  public async Task Defaults_AreSafeForUninitializedProjectionAsync() {
    var item = new SagaItemModel();

    await Assert.That(item.State).IsEqualTo(SagaItemState.Pending)
      .Because("A row that was just inserted but not yet started must report Pending; defaulting to Running would forge progress.");
    await Assert.That(item.ItemIdentifier).IsEqualTo(string.Empty);
    await Assert.That(item.SagaName).IsEqualTo(string.Empty);
    await Assert.That(item.DisplayName).IsNull();
    await Assert.That(item.ErrorMessage).IsNull();
    await Assert.That(item.ErrorDetails).IsNull();
    await Assert.That(item.AttemptCount).IsEqualTo(0);
    await Assert.That(item.StartedAt).IsNull();
    await Assert.That(item.CompletedAt).IsNull();
    await Assert.That(item.FailedAt).IsNull();
  }

  [Test]
  [Arguments(SagaItemState.Pending, false)]
  [Arguments(SagaItemState.Running, false)]
  [Arguments(SagaItemState.Completed, true)]
  [Arguments(SagaItemState.Failed, true)]
  [Arguments(SagaItemState.Skipped, true)]
  public async Task IsTerminal_TrueForCompletedFailedSkipped_FalseOtherwiseAsync(SagaItemState state, bool expected) {
    var item = new SagaItemModel { State = state };

    await Assert.That(item.IsTerminal).IsEqualTo(expected)
      .Because("IsTerminal gates whether saga-completion logic counts the item as 'done'; including Skipped here is intentional — a skipped item must not block the saga from completing.");
  }

  [Test]
  public async Task DisplayName_ExposesUnderlyingValueAsync() {
    var item = new SagaItemModel { DisplayName = "Acme Corp / Q3 budget" };

    await Assert.That(((ISagaItem)item).DisplayName).IsEqualTo("Acme Corp / Q3 budget");
  }

  [Test]
  public async Task IdAndSagaIdAreDistinctFieldsAsync() {
    var sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var itemStreamId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    var item = new SagaItemModel {
      Id = itemStreamId,
      SagaId = sagaId,
    };

    await Assert.That(item.Id).IsEqualTo(itemStreamId);
    await Assert.That(item.SagaId).IsEqualTo(sagaId);
    await Assert.That(item.Id).IsNotEqualTo(item.SagaId)
      .Because("Per-item rows are keyed by the per-item stream id (Id); the link to the parent aggregate is SagaId. Conflating them collapses every item back onto the saga's stream and re-introduces the cross-pod lost-update race.");
  }
}
