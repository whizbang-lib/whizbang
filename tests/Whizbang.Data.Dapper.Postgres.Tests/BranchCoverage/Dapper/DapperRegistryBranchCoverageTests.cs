// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests.BranchCoverage.DapperDriver;

// The rename tool's apply path is [Obsolete] (superseded by the ledger-aware reconcile) but still
// functional; these tests exercise its logging outcomes until it is removed.
#pragma warning disable CS0618

/// <summary>
/// Logging and metrics outcomes of the message type registry populator and the event type rename
/// tool that the existing suites never take, because they construct both without a logger or
/// metrics: drift reported to a logger, a reconciled rename counted by metrics, and the rename
/// tool's "nothing pending" and "applied" reports.
/// </summary>
[Category("Integration")]
public class DapperRegistryBranchCoverageTests : IAsyncDisposable {
  private const string PINNED_ID = "11111111-1111-1111-1111-111111111111";
  private const string OLD_NAME = "Old.Namespace.CoverageEvent, Sample";
  private const string NEW_NAME = "New.Namespace.CoverageEvent, Sample";

  private string? _testDatabaseName;
  private PostgresConnectionFactory? _connectionFactory;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using (var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await adminConnection.OpenAsync();
      await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
    }

    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    }.ConnectionString;
    _connectionFactory = new PostgresConnectionFactory(connectionString);
    await new PostgresSchemaInitializer(connectionString).InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName is null) {
      return;
    }
    try {
      await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await adminConnection.OpenAsync();
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
    } catch (NpgsqlException) {
      // The container drops every database when it stops; a failed drop here only delays that.
    }
    _testDatabaseName = null;
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// A pinned type whose stored name differs from the code's, with no recorded former name, is drift,
  /// and a populator given a logger reports it as a warning naming both names.
  /// </summary>
  [Test]
  public async Task PopulateAsync_UnacknowledgedRename_WarnsAboutDriftAsync() {
    await new DapperMessageTypeRegistryPopulator(_catalog(OLD_NAME), _connectionFactory!).PopulateAsync();
    var logger = new RecordingLogger<DapperMessageTypeRegistryPopulator>();

    await new DapperMessageTypeRegistryPopulator(_catalog(NEW_NAME), _connectionFactory!, logger).PopulateAsync();

    await Assert.That(logger.Contains(LogLevel.Warning, "Pinned id drift")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Warning, OLD_NAME)).IsTrue();
  }

  /// <summary>
  /// An acknowledged rename is counted by the registry metrics even when the populator has no
  /// logger, so renames stay observable on dashboards in a service that logs nothing at startup.
  /// </summary>
  [Test]
  public async Task PopulateAsync_AcknowledgedRenameWithMetricsAndNoLogger_CountsTheRenameAsync() {
    await new DapperMessageTypeRegistryPopulator(_catalog(OLD_NAME), _connectionFactory!).PopulateAsync();
    using var meterFactory = new TestMeterFactory();
    var metrics = new TypeRegistryMetrics(new WhizbangMetrics(meterFactory));
    var renamedCatalog = new FixedCatalog([
      new MessageTypeCatalogEntry(typeof(CoverageMarker), NEW_NAME, "event", PINNED_ID) { FormerNames = [OLD_NAME] }
    ]);

    await new DapperMessageTypeRegistryPopulator(renamedCatalog, _connectionFactory!, logger: null, metrics).PopulateAsync();

    await Assert.That(_observedMaximum(metrics.Renamed)).IsEqualTo(1L);
  }

  /// <summary>A rename tool with a logger reports when the registry and data tables are already in sync.</summary>
  [Test]
  public async Task ExecuteAsync_NothingPending_LogsInSyncAsync() {
    var logger = new RecordingLogger<DapperEventTypeRenameTool>();
    var tool = new DapperEventTypeRenameTool(new FixedCatalog([]), _connectionFactory!, logger);

    await tool.ExecuteAsync();

    await Assert.That(logger.Contains(LogLevel.Information, "No pending renames")).IsTrue();
  }

  /// <summary>A rename tool with a logger reports each rename it applied and the total it applied.</summary>
  [Test]
  public async Task ExecuteAsync_ManualRename_LogsEachRenameAndTheTotalAsync() {
    var logger = new RecordingLogger<DapperEventTypeRenameTool>();
    var tool = new DapperEventTypeRenameTool(new FixedCatalog([]), _connectionFactory!, logger);
    tool.Rename(OLD_NAME, NEW_NAME);

    await tool.ExecuteAsync();

    await Assert.That(logger.Contains(LogLevel.Information, $"Renamed '{OLD_NAME}'")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Information, "Applied 1 rename(s)")).IsTrue();
  }

  private static FixedCatalog _catalog(string clrTypeName) =>
    new FixedCatalog([new MessageTypeCatalogEntry(typeof(CoverageMarker), clrTypeName, "event", PINNED_ID)]);

  /// <summary>The largest value the counter reports at collection; the untouched series report zero.</summary>
  private static long _observedMaximum(PassiveCounter<long> counter) {
    long maximum = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, counter.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => maximum = Math.Max(maximum, measurement));
    listener.Start();
    listener.RecordObservableInstruments();
    return maximum;
  }

  private sealed record CoverageMarker;

  private sealed class FixedCatalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}

file sealed class RecordingLogger<T> : ILogger<T> {
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  public bool Contains(LogLevel level, string fragment) =>
    _entries.Exists(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
    _entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>A meter factory that owns the meters it creates, so a test's instruments are its own.</summary>
file sealed class TestMeterFactory : IMeterFactory {
  private readonly List<Meter> _meters = [];

  public Meter Create(MeterOptions options) {
    var meter = new Meter(options);
    _meters.Add(meter);
    return meter;
  }

  public void Dispose() {
    foreach (var meter in _meters) {
      meter.Dispose();
    }
  }
}
