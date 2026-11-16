// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Observability;

/// <summary>
/// Every framework statement is attributed to the mechanism that ran it, at the two places the framework's commands
/// are made: its own raw commands, and the commands EF Core runs for it. The statement reaches the database leading
/// with <c>/* whizbang:&lt;mechanism&gt; */</c> (read back with <c>current_query()</c>, which returns the text exactly
/// as the server received it), and is counted, timed and its rows recorded under the mechanism.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangCommand.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Observability/WhizbangCommandAttributionInterceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/WhizbangDbContextOptionsExtensions.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
[Category("Shard4")]
public class WhizbangCommandAttributionTests : EFCoreTestBase {
  private const string CURRENT_QUERY = "SELECT current_query()";
  private const string CLAIM_COMMENT = "/* whizbang:claim */ ";

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  [Test]
  public async Task ARawCommandInsideAMechanism_ReachesTheDatabaseAttributed_AndIsRecordedWithItsRowsAsync() {
    await using var connection = await _openAsync();
    using var recorder = new StatementRecorder();
    string? received;
    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      await using (var query = connection.CreateWhizbangCommand()) {
        query.CommandText = CURRENT_QUERY;
        received = (string?)await query.ExecuteScalarAsync();
      }
      await using var rows = connection.CreateWhizbangCommand();
      rows.CommandText = "SELECT generate_series(1, 3)";
      await using var reader = await rows.ExecuteReaderAsync();
      var read = 0;
      while (await reader.ReadAsync()) {
        read++;
      }
      await Assert.That(read).IsEqualTo(3);
    }

    await Assert.That(received).IsEqualTo(CLAIM_COMMENT + CURRENT_QUERY);
    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "claim")).IsEqualTo(2d);
    await Assert.That(recorder.Count(DbStatementMetrics.ROWS, "claim")).IsEqualTo(4d)
      .Because("one row from the first statement, three read from the second");
    await Assert.That(recorder.Count(DbStatementMetrics.DURATION, "claim")).IsGreaterThanOrEqualTo(0d);
  }

  [Test]
  public async Task ARawCommandOutsideAnyMechanism_IsLeftAsWritten_AndRecordedAsUntaggedAsync() {
    await using var connection = await _openAsync();
    using var recorder = new StatementRecorder();
    string? received;
    await using (var query = connection.CreateCoordinatorCommand()) {
      query.CommandText = CURRENT_QUERY;
      received = (string?)await query.ExecuteScalarAsync();
      await Assert.That(query.CommandTimeout).IsEqualTo(CoordinatorCommandTimeout.SECONDS);
    }

    await Assert.That(received).IsEqualTo(CURRENT_QUERY);
    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "untagged")).IsEqualTo(1d)
      .Because("a framework statement no mechanism claimed is a gap, and the gap is counted");
  }

  [Test]
  public async Task ARawCommandIsRecordedOncePerText_NeverForTextItDidNotRun_AndNeverAttributedTwiceAsync() {
    await using var connection = await _openAsync();
    using var recorder = new StatementRecorder();
    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      await using (var never = connection.CreateWhizbangCommand()) {
        never.CommandText = null;
        await Assert.That(never.CommandText).IsEqualTo(string.Empty).Because("no text is given no comment");
        never.CommandText = "SELECT 1";
      }
      await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "claim")).IsEqualTo(0d)
        .Because("a command disposed without running ran no statement");

      var reused = connection.CreateWhizbangCommand();
      reused.CommandText = "SELECT 1";
      _ = await reused.ExecuteNonQueryAsync();
      var attributed = reused.CommandText;
      reused.CommandText = attributed;
      await Assert.That(reused.CommandText).IsEqualTo(CLAIM_COMMENT + "SELECT 1")
        .Because("text that already leads with a framework comment is not given a second one");
      _ = await reused.ExecuteNonQueryAsync();
      await reused.DisposeAsync();
#pragma warning disable S6966 // the second, synchronous dispose is the point: it must record nothing more
      reused.Dispose();
#pragma warning restore S6966
    }

    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "claim")).IsEqualTo(2d)
      .Because("new text after a run is a second statement, and disposing twice records nothing more");
  }

  [Test]
  public async Task CreatingARawCommand_RefusesANullConnectionAsync() {
    await Assert.That(() => WhizbangCommand.CreateWhizbangCommand(null!)).Throws<ArgumentNullException>();
  }

  // The synchronous paths are two of the ways EF Core sends a command, so calling them is the point.
#pragma warning disable S6966
  [Test]
  public async Task InsideAMechanism_EveryWayEFCoreSendsACommand_IsAttributedAndRecordedAsync() {
    await using var context = CreateDbContext();
    using var recorder = new StatementRecorder();
    var received = new List<string>();
    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      received.Add((await context.Database.SqlQueryRaw<string>(CURRENT_QUERY + " AS \"Value\"").ToListAsync()).Single());
      received.Add(context.Database.SqlQueryRaw<string>(CURRENT_QUERY + " AS \"Value\"").ToList().Single());
      received.Add((string)(await _rawScalar(context).ExecuteScalarAsync(_parameters(context)))!);
      received.Add((string)_rawScalar(context).ExecuteScalar(_parameters(context))!);
      _ = await context.Database.ExecuteSqlRawAsync("SELECT 1");
      _ = context.Database.ExecuteSqlRaw("SELECT 1");
    }

    await Assert.That(received.All(r => r.StartsWith(CLAIM_COMMENT, StringComparison.Ordinal))).IsTrue()
      .Because("readers and scalars alike reach the database attributed: " + string.Join(" | ", received));
    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "claim")).IsEqualTo(6d);
    await Assert.That(recorder.Count(DbStatementMetrics.ROWS, "claim")).IsGreaterThanOrEqualTo(4d)
      .Because("each reader read one row and each scalar returned one");
  }
#pragma warning restore S6966

  [Test]
  public async Task OutsideAnyMechanism_TheApplicationsOwnEFCoreCommandsAreLeftAlone_AndNotCountedAsync() {
    await using var context = CreateDbContext();
    using var recorder = new StatementRecorder();

    var received = (await context.Database.SqlQueryRaw<string>(CURRENT_QUERY + " AS \"Value\"").ToListAsync()).Single();
    var nullScalar = await _rawScalar(context, "SELECT NULL::text").ExecuteScalarAsync(_parameters(context));

    await Assert.That(received).DoesNotContain("whizbang:");
    await Assert.That(nullScalar).IsTypeOf<DBNull>();
    await Assert.That(recorder.All).IsEmpty().Because("the application's statements are not the framework's to count");
  }

  [Test]
  public async Task AScalarThatReturnsNothing_CountsNoRowsAsync() {
    await using var context = CreateDbContext();
    using var recorder = new StatementRecorder();
    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      _ = await _rawScalar(context, "SELECT NULL::text").ExecuteScalarAsync(_parameters(context));
    }

    await Assert.That(recorder.Count(DbStatementMetrics.STATEMENTS, "claim")).IsEqualTo(1d);
    await Assert.That(recorder.Count(DbStatementMetrics.ROWS, "claim")).IsEqualTo(0d);
  }

  [Test]
  public async Task UsingTheFrameworksFunctionsTwice_RegistersTheInterceptorOnceAsync() {
    var builder = new DbContextOptionsBuilder();
    builder.UseNpgsql(ConnectionString, npgsql => npgsql.UseWhizbangFunctions().UseWhizbangFunctions());

    var interceptors = builder.Options.FindExtension<CoreOptionsExtension>()!.Interceptors!;

    await Assert.That(interceptors.Count(i => ReferenceEquals(i, WhizbangCommandAttributionInterceptor.Instance)))
      .IsEqualTo(1).Because("a second registration would count every statement twice");
  }

  private static IRelationalCommand _rawScalar(DbContext context, string sql = CURRENT_QUERY) =>
    context.GetService<IRawSqlCommandBuilder>().Build(sql);

  private static RelationalCommandParameterObject _parameters(DbContext context) => new(
    context.GetService<IRelationalConnection>(), null, null, context,
    context.GetService<IRelationalCommandDiagnosticsLogger>());

  /// <summary>
  /// Collects the statement measurements recorded on the flow that created it: the meter is process-wide and tests
  /// run in parallel, and a measurement callback runs on the recording flow.
  /// </summary>
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

    public IReadOnlyList<(string Instrument, double Value, string? Mechanism)> All {
      get {
        lock (_all) {
          return [.. _all];
        }
      }
    }

    public double Count(string instrument, string mechanism) =>
      All.Where(m => m.Instrument == instrument && m.Mechanism == mechanism).Sum(m => m.Value);

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
