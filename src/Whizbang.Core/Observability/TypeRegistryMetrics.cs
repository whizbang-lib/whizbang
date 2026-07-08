using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the pinned-type rename platform's startup registry reconcile. Meter name:
/// <c>Whizbang.TypeRegistry</c>.
/// </summary>
/// <remarks>
/// <para>
/// The reconcile runs once per service startup (see the Dapper/EFCore registry populators and the generated
/// turnkey path). These counters make its outcome queryable + alertable across the fleet rather than only visible
/// in per-service logs:
/// </para>
/// <list type="bullet">
///   <item><description><b>Renamed</b> — a <c>wh_message_type_registry</c> row reconciled old → new because the
///   stored name was a recorded former name in the committed pinned-type ledger (an acknowledged rename).</description></item>
///   <item><description><b>DriftDetected</b> — a stored name differs from the current code name and is <b>not</b> a
///   recorded former name (an un-acknowledged rename). Alert on this being &gt; 0 anywhere.</description></item>
/// </list>
/// <para>Both are tagged by <c>service</c> (the consumer assembly name) so drift can be attributed to a service.</para>
/// </remarks>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TypeRegistryMetricsTests.cs</tests>
public sealed class TypeRegistryMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.TypeRegistry";
#pragma warning restore CA1707

  /// <summary>Registry rows reconciled old → new for an acknowledged rename. Tagged by <c>service</c>.</summary>
  public Counter<long> Renamed { get; }

  /// <summary>Un-acknowledged drift left untouched (stored name is not a recorded former name). Tagged by <c>service</c>.</summary>
  public Counter<long> DriftDetected { get; }

  /// <summary>Initializes a new instance of <see cref="TypeRegistryMetrics"/>.</summary>
  public TypeRegistryMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    Renamed = meter.CreateCounter<long>(
      "whizbang.type_registry.renamed",
      description: "Registry rows reconciled old->new for an acknowledged rename; tagged by service");
    DriftDetected = meter.CreateCounter<long>(
      "whizbang.type_registry.drift_detected",
      description: "Un-acknowledged registry drift left untouched; tagged by service");
  }

  /// <summary>
  /// Records reconcile outcomes for one service startup. No-ops on zero counts. When <paramref name="service"/> is
  /// null it defaults to the running entry assembly's name, so call sites need not resolve it themselves.
  /// </summary>
  /// <param name="renamed">Number of acknowledged renames reconciled.</param>
  /// <param name="driftDetected">Number of un-acknowledged drift rows detected.</param>
  /// <param name="service">The consumer assembly / service name (drift attribution tag). Defaults to the entry assembly.</param>
  public void Record(long renamed, long driftDetected, string? service = null) {
    service ??= System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
    var tag = new KeyValuePair<string, object?>("service", string.IsNullOrEmpty(service) ? "<unknown>" : service);
    if (renamed > 0) {
      Renamed.Add(renamed, tag);
    }
    if (driftDetected > 0) {
      DriftDetected.Add(driftDetected, tag);
    }
  }
}
