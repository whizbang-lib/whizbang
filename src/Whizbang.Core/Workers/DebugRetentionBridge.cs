namespace Whizbang.Core.Workers;

/// <summary>
/// Carries the application's debug-retention option to the SQL layer that acts on it.
/// </summary>
/// <remarks>
/// <para>
/// Debug retention is decided in two places that never met. The completion path reads
/// <c>WorkCoordinatorOptions.DebugMode</c> and marks rows instead of deleting them. The maintenance
/// sweep decides independently, reading <c>wh_settings.debug_mode</c> — a row nothing in the
/// framework wrote, so it stayed at its default.
/// </para>
/// <para>
/// The result is silent and inverted: setting the documented option makes completion retain rows,
/// and the sweep deletes them anyway within one interval because the database still says false.
/// "Keep completed messages for debugging" produced retention that evaporated, and counts taken
/// against it fell while being read — which is the worst possible failure for a diagnostic feature,
/// because it corrupts the measurement it exists to enable rather than refusing to run.
/// </para>
/// <para>
/// Both directions are pushed. Writing only <c>true</c> would leave a service whose debug retention
/// was switched off holding a stale <c>true</c>, so its sweep would never purge again and the inbox
/// would grow without bound — a worse outcome than the problem being diagnosed.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/maintenance</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DebugRetentionBridgeTests.cs</tests>
public static class DebugRetentionBridge {

  /// <summary>The <c>wh_settings</c> key the maintenance sweep reads.</summary>
  public static string SettingKey => "debug_mode";

  /// <summary>Renders the option as the text the sweep will cast to BOOLEAN.</summary>
  /// <param name="debugMode">Whether completed rows should be retained.</param>
  /// <returns>An exact lowercase literal.</returns>
  /// <remarks>
  /// Deliberately not <c>bool.ToString()</c>, which yields "True". Postgres accepts that today, but
  /// the value is cast (<c>setting_value::BOOLEAN</c>) and an exact literal removes the question.
  /// </remarks>
  public static string SettingValueFor(bool debugMode) => debugMode ? "true" : "false";

  /// <summary>Whether the setting must be written for this option value.</summary>
  /// <param name="debugMode">Whether completed rows should be retained.</param>
  /// <returns>Always true — both directions must propagate.</returns>
  public static bool RequiresSync(bool debugMode) => true;
}
