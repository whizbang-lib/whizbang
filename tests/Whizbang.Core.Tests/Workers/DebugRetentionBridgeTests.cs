using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The application's debug-retention option must reach the SQL layer that acts on it.
/// </summary>
/// <remarks>
/// <para>
/// Debug retention is two switches that never met. <c>WorkCoordinatorOptions.DebugMode</c> governs
/// the completion path in C#, so rows are marked rather than deleted. The maintenance sweep decides
/// independently, reading <c>wh_settings.debug_mode</c> — a row nothing in the framework writes.
/// </para>
/// <para>
/// The failure is silent and the wrong way round. Setting the documented option makes completion
/// retain rows, and the sweep then deletes them anyway within one interval, because the database
/// still says false. An operator reading "keep completed messages for debugging" gets retention
/// that quietly evaporates, and any count taken against it falls while being read.
/// </para>
/// <para>
/// Observed while measuring a bulk import: retained rows read 210,720, then 201,204, then 137,026,
/// falling while the workload was still producing. Every ratio computed from that denominator was
/// wrong, and nothing logged that debug-retained rows had been removed.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/DebugRetentionBridge.cs</code-under-test>
[Category("Workers")]
public class DebugRetentionBridgeTests {

  [Test]
  public async Task TheSqlSettingMirrorsTheOptionAsync() {
    await Assert.That(DebugRetentionBridge.SettingValueFor(debugMode: true)).IsEqualTo("true");
    await Assert.That(DebugRetentionBridge.SettingValueFor(debugMode: false)).IsEqualTo("false");
  }

  [Test]
  public async Task TheValueIsLowercaseForPostgresBooleanCastAsync() {
    // wh_settings stores text and the sweep casts it: setting_value::BOOLEAN. .NET's default
    // Boolean.ToString() yields "True", which is accepted by Postgres today but is not something
    // to depend on — an exact lowercase literal is unambiguous.
    await Assert.That(DebugRetentionBridge.SettingValueFor(true)).IsEqualTo("true")
      .Because("the sweep casts this text to BOOLEAN, so the written form has to be one Postgres "
             + "parses the same way every time");
    await Assert.That(DebugRetentionBridge.SettingValueFor(true)).IsNotEqualTo("True");
  }

  [Test]
  public async Task SyncIsRequiredWheneverTheOptionIsSetAsync() {
    // Both directions must be pushed. Only writing "true" would leave a service that had debug
    // retention switched OFF still holding a stale true, so its sweep would never purge again and
    // the inbox would grow without bound.
    await Assert.That(DebugRetentionBridge.RequiresSync(debugMode: true)).IsTrue();
    await Assert.That(DebugRetentionBridge.RequiresSync(debugMode: false)).IsTrue()
      .Because("turning debug retention OFF must also propagate, or a stale true silently disables "
             + "the purge forever and the leak is worse than the problem it was set to diagnose");
  }

  [Test]
  public async Task TheSettingKeyMatchesWhatTheSweepReadsAsync() {
    await Assert.That(DebugRetentionBridge.SettingKey).IsEqualTo("debug_mode")
      .Because("the sweep looks up this exact key; a mismatch reintroduces the silent disconnect "
             + "this type exists to close");
  }
}
