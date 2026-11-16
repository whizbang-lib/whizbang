// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper data path attributes its statements as the other drivers do: a coordinator call inside a mechanism is
/// recorded under the mechanism, one outside every mechanism is recorded as untagged, so no driver silently does less.
/// Dapper creates its own commands, so they are reached through the connection it is handed
/// (<c>CoordinatorConnectionScope.Attributed</c>).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/AttributedDbConnection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorConnectionScope.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
public class DapperCommandAttributionTests : PostgresTestBase {
  private DapperWorkCoordinator _coordinator() => new(ConnectionString,
    new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default }, NullLogger<DapperWorkCoordinator>.Instance);

  [Test]
  public async Task ACoordinatorCallInsideAMechanism_IsRecordedUnderIt_AndOneOutside_AsUntaggedAsync() {
    var coordinator = _coordinator();
    using var recorder = new StatementRecorder();

    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      _ = await coordinator.CountOutstandingWorkAsync(Guid.CreateVersion7());
    }
    var inside = recorder.Count(DbStatementMetrics.STATEMENTS, "claim");
    _ = await coordinator.CountOutstandingWorkAsync(Guid.CreateVersion7());

    await Assert.That(inside).IsGreaterThanOrEqualTo(1d).Because("the Dapper call's statements carry the mechanism");
    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "untagged")).IsGreaterThanOrEqualTo(1d)
      .Because("a framework statement with no mechanism is counted as untagged, so the gap shows");
  }

  [Test]
  public async Task AStatementDapperSendsInsideAMechanism_ReachesTheServerAttributedAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var scope = await CoordinatorConnectionScope.AcquireAsync(ConnectionString, CancellationToken.None);
    await using (scope) {
      string? received;
      using (DbMechanismScope.Begin(DbMechanism.Maintenance)) {
        received = await global::Dapper.SqlMapper.ExecuteScalarAsync<string>(scope.Attributed, "SELECT current_query()");
      }

      await Assert.That(received).IsEqualTo("/* whizbang:maintenance */ SELECT current_query()");
    }
  }

  // The synchronous members are part of what the wrapper forwards, so calling them is the point.
#pragma warning disable S6966
  [Test]
  public async Task TheAttributedConnection_ForwardsEverythingToTheConnection_AndDisposesNothingAsync() {
    await using var inner = new NpgsqlConnection(ConnectionString);
    var attributed = new AttributedDbConnection(inner);
    attributed.ConnectionString = ConnectionString;
    await attributed.OpenAsync();
    attributed.Close();
    attributed.Open();

    await Assert.That(attributed.ConnectionString).IsEqualTo(inner.ConnectionString);
    await Assert.That(attributed.Database).IsEqualTo(inner.Database);
    await Assert.That(attributed.DataSource).IsEqualTo(inner.DataSource);
    await Assert.That(attributed.ServerVersion).IsEqualTo(inner.ServerVersion);
    await Assert.That(attributed.State).IsEqualTo(ConnectionState.Open);
    attributed.ChangeDatabase(inner.Database);
    await using (var transaction = await attributed.BeginTransactionAsync(IsolationLevel.ReadCommitted)) {
      await Assert.That(transaction.Connection).IsSameReferenceAs(inner);
    }
    await using (var command = attributed.CreateCommand()) {
      await Assert.That(command.Connection).IsSameReferenceAs(inner);
    }
    await using (var viaBase = ((System.Data.Common.DbConnection)attributed).CreateWhizbangCommand()) {
      await Assert.That(viaBase.Connection).IsSameReferenceAs(inner);
    }
    await attributed.DisposeAsync();

    await Assert.That(inner.State).IsEqualTo(ConnectionState.Open).Because("the connection belongs to whoever acquired it");
  }
#pragma warning restore S6966

  /// <summary>Collects the statement measurements recorded on the flow that created it.</summary>
  private sealed class StatementRecorder : IDisposable {
    private static readonly AsyncLocal<object?> _flow = new();
    private readonly object _marker = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, string? Mechanism)> _all = [];

    public StatementRecorder() {
      _flow.Value = _marker;
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter.Name == DbStatementMetrics.METER_NAME) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _add(i.Name, v, tags));
      _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => _add(i.Name, v, tags));
      _listener.Start();
    }

    public double Count(string instrument, string mechanism) {
      lock (_all) {
        return _all.Where(m => m.Instrument == instrument && m.Mechanism == mechanism).Sum(m => m.Value);
      }
    }

    private void _add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      if (!ReferenceEquals(_flow.Value, _marker)) {
        return;
      }
      string? mechanism = null;
      foreach (var tag in tags) {
        if (tag.Key == DbStatementMetrics.MECHANISM_TAG) {
          mechanism = tag.Value as string;
        }
      }
      lock (_all) {
        _all.Add((instrument, value, mechanism));
      }
    }

    public void Dispose() => _listener.Dispose();
  }
}
