using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The turnkey <see cref="WhizbangMeters.All"/> list names the Postgres driver's meter as a
/// string literal (Core cannot reference the constant without inverting the dependency graph).
/// This test is the cross-assembly lock that keeps the literal honest: a rename of
/// <see cref="NotifyMetrics.METER_NAME"/> that forgets the registry fails HERE.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/WhizbangMeters.cs</code-under-test>
[Category("Shard2")]
public class NotifyMeterRegistrationTests {
  [Test]
  public async Task TurnkeyList_CarriesTheNotifyMeterName_MatchingTheOwningConstantAsync() {
    await Assert.That(WhizbangMeters.All.Count(n => n == NotifyMetrics.METER_NAME)).IsEqualTo(1)
      .Because("a consumer wiring AddMeter(WhizbangMeters.All) must export driver instruments "
               + "without a hand-list — and exactly once");
  }
}
