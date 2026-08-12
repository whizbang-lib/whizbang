using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Observability;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The turnkey <see cref="WhizbangMeters.All"/> list names this package's meter as a string
/// literal (Core cannot reference the constant without inverting the dependency graph). This
/// test is the cross-assembly lock that keeps the literal honest: a rename of
/// <see cref="SagaMetrics.METER_NAME"/> that forgets the registry fails HERE.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/WhizbangMeters.cs</code-under-test>
public class SagaMeterRegistrationTests {
  [Test]
  public async Task TurnkeyList_CarriesTheSagaMeterName_MatchingTheOwningConstantAsync() {
    await Assert.That(WhizbangMeters.All.Count(n => n == SagaMetrics.METER_NAME)).IsEqualTo(1)
      .Because("a consumer wiring AddMeter(WhizbangMeters.All) must export saga instruments "
               + "without a hand-list — and exactly once");
  }
}
