using OpenTelemetry.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Observability.DependencyInjection;

/// <summary>
/// Subscribes an OpenTelemetry metrics pipeline to every meter Whizbang publishes.
/// </summary>
/// <remarks>
/// <para>
/// A meter that nothing subscribes to is not an error anywhere. The instrument is created, the
/// counters increment, and every recorded value is discarded at the subscription boundary. Nothing
/// logs, nothing throws, and the application looks correctly instrumented from the inside. The gap
/// is only visible by comparing what the framework declares against what actually arrives in a
/// metrics backend, which nobody does until they need a signal that is not there.
/// </para>
/// <para>
/// That is why hand-listing meter names fails silently and keeps failing: the list is written once
/// against the meters that existed that day, and every meter added afterwards is invisible. Observed
/// in a real deployment as sixteen of twenty-one declared meters emitting nothing for the life of
/// the environment, including the dead-letter, maintenance, poison-message and startup meters. Those
/// are precisely the ones an operator reaches for when something is wrong, so the absence is
/// discovered at the worst possible moment.
/// </para>
/// <para>
/// <see cref="WhizbangMeters.All"/> is the framework's own list and grows as packages self-register,
/// so subscribing to it keeps a consumer correct without edits. Call this instead of naming meters.
/// </para>
/// </remarks>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/WhizbangMeterRegistrationTests.cs</tests>
public static class WhizbangMeterRegistration {

  /// <summary>
  /// Subscribes the metrics pipeline to every meter in <see cref="WhizbangMeters.All"/>.
  /// </summary>
  /// <param name="builder">The metrics builder to configure.</param>
  /// <returns>The same builder, for chaining.</returns>
  /// <remarks>
  /// Call this AFTER the optional Whizbang packages a service uses have loaded, because each one
  /// self-registers its meter and <see cref="WhizbangMeters.All"/> is read as a snapshot. In the
  /// normal case of configuring telemetry during host construction the packages are already loaded
  /// and there is nothing to arrange; the ordering only matters if a package is loaded lazily.
  /// </remarks>
  /// <example>
  /// <code>
  /// builder.Services.AddOpenTelemetry()
  ///   .WithMetrics(metrics => metrics
  ///     .AddAspNetCoreInstrumentation()
  ///     .AddWhizbangInstrumentation());
  /// </code>
  /// </example>
  public static MeterProviderBuilder AddWhizbangInstrumentation(this MeterProviderBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddMeter([.. WhizbangMeters.All]);
  }
}
