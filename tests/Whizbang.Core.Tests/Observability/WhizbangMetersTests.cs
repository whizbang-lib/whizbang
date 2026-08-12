using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The canonical meter registry: consumers wire ONE line
/// (<c>.AddMeter(WhizbangMeters.All)</c>) instead of hand-maintaining an allow-list of meter
/// name strings. Observed live: a consumer's hand-list carried 5 of the framework's 16 meters,
/// so every instrument on the other 11 — including an entire subsystem's observability shipped
/// that same week — silently never reached the collector. A registry the framework maintains is
/// the only shape that cannot drift.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/WhizbangMeters.cs</code-under-test>
public class WhizbangMetersTests {

  /// <summary>
  /// The drift lock, and the reason this class can exist safely: every <c>METER_NAME</c>
  /// constant in the Core assembly must appear in <see cref="WhizbangMeters.All"/>. A new
  /// metrics class whose author forgets the registry fails THIS test, not a consumer's
  /// dashboard three weeks later. (Reflection is fine here — tests are not AOT-bound.)
  /// </summary>
  [Test]
  public async Task All_CoversEveryMeterNameConstantInTheCoreAssemblyAsync() {
    var coreMeterNames = typeof(WhizbangMeters).Assembly.GetTypes()
      .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
      .Where(f => f is { IsLiteral: true, Name: "METER_NAME" } && f.FieldType == typeof(string))
      .Select(f => (string)f.GetRawConstantValue()!)
      .Distinct()
      .ToList();

    await Assert.That(coreMeterNames.Count).IsGreaterThanOrEqualTo(14)
      .Because("the census that motivated this registry found 14 meters in Core — fewer means "
               + "the discovery reflection broke, and the lock would be vacuous");

    var missing = coreMeterNames.Where(n => !WhizbangMeters.All.Contains(n)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because("every framework meter must be exported by the turnkey list — an instrument "
               + "nobody can see is work the system does for an audience of zero");
  }

  [Test]
  public async Task Register_IsIdempotent_AndSurfacesTheNameInAllAsync() {
    var name = $"Whizbang.Test.{Guid.NewGuid():N}";

    WhizbangMeters.Register(name);
    WhizbangMeters.Register(name);   // package ModuleInitializers may race or re-run in tests

    await Assert.That(WhizbangMeters.All.Count(n => n == name)).IsEqualTo(1)
      .Because("duplicate registrations must not produce duplicate AddMeter subscriptions");
  }

  [Test]
  public async Task All_IsStableAndContainsNoBlanksAsync() {
    var all = WhizbangMeters.All;

    await Assert.That(all.All(n => !string.IsNullOrWhiteSpace(n))).IsTrue();
    await Assert.That(all.Distinct().Count()).IsEqualTo(all.Count)
      .Because("a duplicate meter name would double-subscribe every instrument on it");
    await Assert.That(all).IsEquivalentTo(WhizbangMeters.All)
      .Because("two reads must agree — consumers snapshot this into OTel setup at startup");
  }
}
