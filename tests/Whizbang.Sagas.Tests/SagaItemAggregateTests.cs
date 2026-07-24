using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks <see cref="SagaItemAggregate"/>'s data shape. Returned by
/// <c>SagaItemRepository.GetAggregateForSagaAsync</c> as a single
/// snapshot the saga's completion logic reads — the four fields are the
/// load-bearing contract.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemAggregateTests {

  [Test]
  public async Task Constructor_AssignsAllFieldsAsync() {
    var agg = new SagaItemAggregate(Total: 100, Completed: 60, Failed: 5, InProgress: 35);

    await Assert.That(agg.Total).IsEqualTo(100);
    await Assert.That(agg.Completed).IsEqualTo(60);
    await Assert.That(agg.Failed).IsEqualTo(5);
    await Assert.That(agg.InProgress).IsEqualTo(35);
  }

  [Test]
  public async Task RecordEquality_SameValues_AreEqualAsync() {
    var a = new SagaItemAggregate(Total: 10, Completed: 4, Failed: 1, InProgress: 5);
    var b = new SagaItemAggregate(Total: 10, Completed: 4, Failed: 1, InProgress: 5);

    await Assert.That(a).IsEqualTo(b)
      .Because("Record value equality is the contract: two aggregates representing the same state must compare equal.");
  }

  [Test]
  public async Task RecordEquality_DifferentValues_AreNotEqualAsync() {
    var a = new SagaItemAggregate(Total: 10, Completed: 4, Failed: 1, InProgress: 5);
    var b = new SagaItemAggregate(Total: 10, Completed: 5, Failed: 0, InProgress: 5);

    await Assert.That(a).IsNotEqualTo(b);
  }
}
