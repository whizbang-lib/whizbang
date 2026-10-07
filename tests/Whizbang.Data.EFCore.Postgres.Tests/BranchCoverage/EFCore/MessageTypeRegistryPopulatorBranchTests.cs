// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCoreMessageTypeRegistryPopulator"/>'s optional logger and
/// metrics: an acknowledged rename and an unacknowledged drift reconcile the same way with or
/// without them, the metered host records both counts, and the empty catalog skips on either.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreMessageTypeRegistryPopulator.cs</code-under-test>
[Category("Shard2")]
public class MessageTypeRegistryPopulatorBranchTests : EFCoreTestBase {

  private const string RENAMED_ID = "11111111-1111-1111-1111-111111111111";
  private const string DRIFTED_ID = "22222222-2222-2222-2222-222222222222";

  [Test]
  public async Task EmptyCatalog_SkipsWithOrWithoutALoggerAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var logger = new CapturingLogger();

    await new EFCoreMessageTypeRegistryPopulator(new Catalog([]), dataSource).PopulateAsync();
    await new EFCoreMessageTypeRegistryPopulator(new Catalog([]), dataSource, logger).PopulateAsync();

    await Assert.That(logger.Messages.Any(m => m.Contains("catalog is empty", StringComparison.Ordinal))).IsTrue();
    await Assert.That(await _registryCountAsync()).IsEqualTo(0);
  }

  // Unlogged and unmetered: the rename is still reconciled in place and the drift still left
  // alone; the outcome must not depend on observability being wired.
  [Test]
  public async Task RenameAndDrift_WithoutLoggerOrMetrics_ReconcileTheSameAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await _seedOldNamesAsync(dataSource);

    await new EFCoreMessageTypeRegistryPopulator(_currentCatalog(), dataSource).PopulateAsync();

    var names = await _registryNamesAsync();
    await Assert.That(names).Contains("New.Renamed, Sample")
      .Because("the acknowledged rename is reconciled in place");
    await Assert.That(names).Contains("Old.Drifted, Sample")
      .Because("an unacknowledged drift is reported, never overwritten");
  }

  [Test]
  public async Task RenameAndDrift_WithLoggerAndMetrics_LogsAndCountsBothAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await _seedOldNamesAsync(dataSource);
    var logger = new CapturingLogger();
    var metrics = new TypeRegistryMetrics(new WhizbangMetrics(
      new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var totals = new Dictionary<Instrument, long>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.Renamed.Meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (totals) {
        totals[instrument] = totals.GetValueOrDefault(instrument) + value;
      }
    });
    listener.Start();

    await new EFCoreMessageTypeRegistryPopulator(_currentCatalog(), dataSource, logger, metrics).PopulateAsync();
    listener.RecordObservableInstruments();

    await Assert.That(logger.Messages.Any(m => m.Contains("Reconciled acknowledged rename", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Messages.Any(m => m.Contains("Pinned id drift", StringComparison.Ordinal))).IsTrue();
    await Assert.That(totals.GetValueOrDefault(metrics.Renamed.Instrument)).IsEqualTo(1L);
    await Assert.That(totals.GetValueOrDefault(metrics.DriftDetected.Instrument)).IsEqualTo(1L);
  }

  // ===== Helpers =====

  private static async Task _seedOldNamesAsync(NpgsqlDataSource dataSource) =>
    await new EFCoreMessageTypeRegistryPopulator(new Catalog([
      new MessageTypeCatalogEntry(typeof(RenamedEvent), "Old.Renamed, Sample", "event", RENAMED_ID),
      new MessageTypeCatalogEntry(typeof(DriftedEvent), "Old.Drifted, Sample", "event", DRIFTED_ID),
    ]), dataSource).PopulateAsync();

  private static Catalog _currentCatalog() => new([
    new MessageTypeCatalogEntry(typeof(RenamedEvent), "New.Renamed, Sample", "event", RENAMED_ID) {
      FormerNames = ["Old.Renamed, Sample"],
    },
    new MessageTypeCatalogEntry(typeof(DriftedEvent), "New.Drifted, Sample", "event", DRIFTED_ID),
  ]);

  private async Task<int> _registryCountAsync() => (await _registryNamesAsync()).Count;

  private async Task<List<string>> _registryNamesAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT clr_type_name FROM wh_message_type_registry";
    var names = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  private sealed record RenamedEvent;

  private sealed record DriftedEvent;

  private sealed class Catalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<EFCoreMessageTypeRegistryPopulator> {
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_messages) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_messages) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
