// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Metrics of the archive coordinator's orphan sweep (#2367). Meter name: <c>Whizbang.Archive.AzureBlob</c>, kept when the
/// coordinator moved into the Postgres driver (#2503) so dashboards and meter subscriptions keep working.
/// </summary>
/// <remarks>
/// <b>SweepsRefused</b> counts every sweep that ran with a deletion delay set and deleted nothing because it could not
/// prove the container is this database's alone, tagged <c>reason</c>: <c>unknown_location</c>, <c>foreign_marker</c>
/// or <c>marker_unclaimable</c> (<see cref="PostgresObjectArchiveStore.SweepRefusal"/>). Every reason exists at zero from
/// construction.
/// </remarks>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#one-database-per-container</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobArchiveStoreOwnerMarkerTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ObjectArchiveCoordinatorTests.cs:ArchiveSweepMetrics_KeepsItsMeterName_AndIsListedInWhizbangMetersAsync</tests>
public sealed class ArchiveSweepMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Archive.AzureBlob";
#pragma warning restore CA1707

  /// <summary>Orphan sweeps refused, tagged by <c>reason</c>.</summary>
  public PassiveCounter<long> SweepsRefused { get; }

  /// <summary>Creates the metrics on the host's meter factory.</summary>
  /// <param name="whizbangMetrics">The host's metrics root.</param>
  public ArchiveSweepMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    SweepsRefused = meter.CreatePassiveCounter<long>(
      "whizbang.archive.orphan_sweep.refused",
      description: "Orphan sweeps that deleted nothing because the container could not be proven this database's, tagged by reason");
    SweepsRefused.Touch("reason", _reasons);
  }

  private static readonly string[] _reasons = [
    PostgresObjectArchiveStore.SweepRefusal.UNKNOWN_LOCATION,
    PostgresObjectArchiveStore.SweepRefusal.FOREIGN_MARKER,
    PostgresObjectArchiveStore.SweepRefusal.MARKER_UNCLAIMABLE,
  ];
}
