using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for <see cref="SagaItemModel"/>'s explicit <see cref="ISagaItem.Status"/> mapping.
/// <see cref="SagaItemModelTests"/> already proves <c>DisplayName</c> round-trips through the
/// interface the same way; nothing in that file (or anywhere in the concrete-typed
/// <c>SagaApplyHelper</c>, which reads/writes <see cref="SagaItemModel.State"/> directly) ever
/// reads <c>Status</c> off an <see cref="ISagaItem"/>-typed reference.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemModelCoverageTests {

  // SagaLiveProgressResolvers and any other generic consumer that only knows ISagaItem read
  // Status, not the concrete State property. If the explicit mapping ever drifted from State --
  // e.g. a copy-paste that hardcoded a value, or forgot to update after a rename -- per-item
  // progress reported through the interface would silently disagree with the concrete row, and
  // a dashboard built on the interface would show stale or wrong status for a real item.
  [Test]
  public async Task ISagaItem_Status_MapsToTheConcreteStateAsync() {
    var item = new SagaItemModel { State = SagaItemState.Failed };

    await Assert.That(((ISagaItem)item).Status).IsEqualTo(SagaItemState.Failed)
      .Because("the explicit ISagaItem.Status mapping must always mirror State -- a generic consumer reading through the interface must see exactly what the concrete row reports");
  }
}
