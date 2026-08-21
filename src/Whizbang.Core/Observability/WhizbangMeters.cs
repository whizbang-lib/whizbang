using System.Collections.Generic;
using System.Linq;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Observability;

/// <summary>
/// The canonical registry of every meter the framework publishes — the turnkey answer to
/// "which meter names do I subscribe?". Wire it once:
/// <code>
/// .WithMetrics(metrics =&gt; metrics.AddMeter(WhizbangMeters.All.ToArray()))
/// </code>
/// and every framework meter — including ones added by future versions — reaches the collector.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the alternative demonstrably fails: a consumer hand-maintaining an
/// allow-list of meter name strings carried 5 of the framework's 16 meters, so every instrument
/// on the other 11 — an entire subsystem's observability among them — was silently never
/// exported. The list an author must update lives HERE, beside the meters, and a Core test
/// reflects over every <c>METER_NAME</c> constant to fail the build when one is missing.
/// </para>
/// <para>
/// Meters that live in optional packages (sagas, database drivers) appear as string literals —
/// Core cannot reference their constants without inverting the dependency graph, and load-time
/// self-registration is unreliable here (consumers snapshot this list into OTel setup at
/// startup, before optional modules have necessarily initialized). Subscribing to a meter the
/// process never creates is a no-op, so listing everything is free; each package's test suite
/// locks its literal to the real constant so a rename cannot desync silently.
/// </para>
/// </remarks>
/// <docs>fundamentals/observability</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/WhizbangMetersTests.cs</tests>
public static class WhizbangMeters {
  private static readonly object _lock = new();

  // Core's own meters, by reference to each owner's METER_NAME — a rename cannot desync this
  // list, and the reflection drift-lock test catches an addition that forgets it.
  private static readonly List<string> _names = [
    DeadLetterMetrics.METER_NAME,
    DispatcherMetrics.METER_NAME,
    EventCategoryMetrics.METER_NAME,
    LifecycleCoordinatorMetrics.METER_NAME,
    LifecycleMetrics.METER_NAME,
    MaintenanceMetrics.METER_NAME,
    MessageDiscardPolicy.METER_NAME,
    PerspectiveMetrics.METER_NAME,
    PinnedPoolMetrics.METER_NAME,
    PoisonMessageDetector.METER_NAME,
    StartupPipelineMetrics.METER_NAME,
    StreamIntegrityMetrics.METER_NAME,
    BacklogAgeMetrics.METER_NAME,
    TableStatisticsMetrics.METER_NAME,
    TransportDeadLetterDrainWorker.METER_NAME,
    TransportMetrics.METER_NAME,
    TypeRegistryMetrics.METER_NAME,
    WorkCoordinatorMetrics.METER_NAME,
    // Optional-package meters, as literals (see remarks). Each package's tests lock its
    // literal to the owning METER_NAME constant.
    "Whizbang.Sagas",                    // Whizbang.Sagas — SagaMetrics
    "Whizbang.Postgres.Notifications",   // Whizbang.Data.Postgres — NotifyMetrics
  ];

  /// <summary>
  /// Every registered meter name: Core's built-ins plus whatever loaded packages have
  /// self-registered. Returns a snapshot — safe to enumerate while packages register.
  /// </summary>
  public static IReadOnlyList<string> All {
    get {
      lock (_lock) {
        return [.. _names];
      }
    }
  }

  /// <summary>
  /// Adds a package's meter name to <see cref="All"/>. Idempotent — ModuleInitializers can
  /// run more than once across test hosts, and a duplicate would double-subscribe every
  /// instrument on the meter.
  /// </summary>
  public static void Register(string meterName) {
    lock (_lock) {
      if (!_names.Contains(meterName)) {
        _names.Add(meterName);
      }
    }
  }
}
