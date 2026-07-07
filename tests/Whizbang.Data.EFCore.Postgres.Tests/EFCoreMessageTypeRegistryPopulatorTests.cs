using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="EFCoreMessageTypeRegistryPopulator"/> — the
/// NpgsqlDataSource-based twin of DapperMessageTypeRegistryPopulator. Both delegate to
/// the <c>reconcile_message_type_registry</c> PL/pgSQL function (migration 040), so this
/// suite mirrors the Dapper test cases: pinned types keyed by pinned_id, unpinned types
/// keyed by clr_type_name, drift detection logs but does not overwrite, and the
/// empty-catalog no-op branch. The schema (table + function) is installed by
/// <c>EnsureWhizbangDatabaseInitializedAsync</c> in <see cref="EFCoreTestBase"/>; the
/// registry starts empty because the base class does not pass a service provider.
/// </summary>
public class EFCoreMessageTypeRegistryPopulatorTests : EFCoreTestBase {

  [Test]
  public async Task Constructor_NullCatalog_ThrowsArgumentNullExceptionAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

    var ex = Assert.Throws<ArgumentNullException>(
      () => _ = new EFCoreMessageTypeRegistryPopulator(null!, dataSource));

    await Assert.That(ex!.ParamName).IsEqualTo("catalog");
  }

  [Test]
  public async Task Constructor_NullDataSource_ThrowsArgumentNullExceptionAsync() {
    var ex = Assert.Throws<ArgumentNullException>(
      () => _ = new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([]), null!));

    await Assert.That(ex!.ParamName).IsEqualTo("dataSource");
  }

  [Test]
  public async Task PopulateAsync_InsertsPinnedTypesAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.PinnedEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinnedCmd), "Sample.PinnedCommand, Sample", "command", "22222222-2222-2222-2222-222222222222"),
    ]);

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows.Any(r => r.PinnedId == new Guid("11111111-1111-1111-1111-111111111111") && r.ClrTypeName == "Sample.PinnedEvent, Sample" && r.Kind == "event")).IsTrue();
    await Assert.That(rows.Any(r => r.PinnedId == new Guid("22222222-2222-2222-2222-222222222222") && r.ClrTypeName == "Sample.PinnedCommand, Sample" && r.Kind == "command")).IsTrue();
  }

  [Test]
  public async Task PopulateAsync_InsertsUnpinnedTypesWithNullPinnedIdAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.UnpinnedEvent, Sample", "event", null),
    ]);

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].PinnedId).IsNull();
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Sample.UnpinnedEvent, Sample");
    await Assert.That(rows[0].Kind).IsEqualTo("event");
  }

  [Test]
  public async Task PopulateAsync_IsIdempotent_ReRunKeepsTypeIdAndDoesNotDuplicateAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.PinnedEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource);
    await populator.PopulateAsync();
    var firstRow = (await _queryRegistryAsync()).Single();

    await populator.PopulateAsync();
    var secondRow = (await _queryRegistryAsync()).Single();

    await Assert.That(secondRow.TypeId).IsEqualTo(firstRow.TypeId)
      .Because("Re-running reconciliation must update in place, never re-key the row.");
    await Assert.That(secondRow.UpdatedAt >= firstRow.UpdatedAt).IsTrue();
  }

  [Test]
  public async Task PopulateAsync_PinnedRenameDetected_DoesNotOverwriteAndLogsDriftWarningAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

    // Initial run: pinned type with CLR name "Old.Namespace.FooEvent"
    var catalog1 = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Old.Namespace.FooEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);
    await new EFCoreMessageTypeRegistryPopulator(catalog1, dataSource).PopulateAsync();

    // Subsequent run: same pinned_id, different CLR name (namespace was renamed)
    var catalog2 = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "New.Namespace.FooEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);
    var logger = new CapturingLogger<EFCoreMessageTypeRegistryPopulator>();
    await new EFCoreMessageTypeRegistryPopulator(catalog2, dataSource, logger).PopulateAsync();

    // Registry row remains pointing at the old CLR name — drift is reported, not reconciled.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Old.Namespace.FooEvent, Sample");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid("11111111-1111-1111-1111-111111111111"));

    var driftWarning = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning && e.Message.Contains("Pinned id drift", StringComparison.Ordinal));
    await Assert.That(driftWarning).IsNotNull()
      .Because("The drift_detected branch must warn the operator to run the rename tool.");
    await Assert.That(driftWarning!.Message).Contains("Old.Namespace.FooEvent, Sample");
    await Assert.That(driftWarning!.Message).Contains("New.Namespace.FooEvent, Sample");
  }

  [Test]
  public async Task PopulateAsync_AcknowledgedRename_ReconcilesOldToNewInPlaceAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    const string id = "11111111-1111-1111-1111-111111111111";

    // Initial run: registry holds the OLD CLR name.
    await new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Old.Namespace.FooEvent, Sample", "event", id),
    ]), dataSource).PopulateAsync();

    // Re-populate: same pinned_id, NEW name, with the OLD name recorded as a former name (acknowledged rename).
    var logger = new CapturingLogger<EFCoreMessageTypeRegistryPopulator>();
    await new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "New.Namespace.FooEvent, Sample", "event", id)
        { FormerNames = ["Old.Namespace.FooEvent, Sample"] },
    ]), dataSource, logger).PopulateAsync();

    // Reconciled in place: row holds the NEW name, same pinned_id; an info log records the rename.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("New.Namespace.FooEvent, Sample");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid(id));

    var renameLog = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Information && e.Message.Contains("Reconciled acknowledged rename", StringComparison.Ordinal));
    await Assert.That(renameLog).IsNotNull();
  }

  [Test]
  public async Task PopulateAsync_PinnedDottedEncoding_NormalizedToPlusAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    const string id = "11111111-1111-1111-1111-111111111111";

    // Legacy state: stored '.'-nested (the old MessageTypeCatalogGenerator wrote the C# display form).
    await new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts.Projection", "perspective", id),
    ]), dataSource).PopulateAsync();

    // Current catalog reports the CLR '+'-nested form for the SAME pinned type.
    await new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts+Projection", "perspective", id),
    ]), dataSource).PopulateAsync();

    // Reconciled in place ('.'-encoding -> '+'), NOT flagged as drift.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Acme.Domain.OrderContracts+Projection");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid(id));
  }

  [Test]
  public async Task PopulateAsync_DottedEncoding_WithPreexistingPlusRow_DedupsWithoutCollisionAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    const string dottedId = "11111111-1111-1111-1111-111111111111";

    // A stale '.'-row (this pinned id) AND the canonical '+'-row (a different identity)
    // already coexist. A naive rename '.'->'+' would violate the clr_type_name primary key.
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var insert = connection.CreateCommand();
      insert.CommandText = @"
        INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at) VALUES
        ('Acme.Domain.OrderContracts.Projection', @dotted, 'perspective', NOW()),
        ('Acme.Domain.OrderContracts+Projection', gen_random_uuid(), 'perspective', NOW())";
      insert.Parameters.AddWithValue("dotted", new Guid(dottedId));
      await insert.ExecuteNonQueryAsync();
    }

    await new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts+Projection", "perspective", dottedId),
    ]), dataSource).PopulateAsync();

    // The stale '+' duplicate is dropped and the pinned row is normalized — exactly one '+' row remains.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Acme.Domain.OrderContracts+Projection");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid(dottedId));
  }

  [Test]
  public async Task PopulateAsync_MixedPinnedAndUnpinned_BothInsertedAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Unpinned, Sample", "event", null),
      new MessageTypeCatalogEntry(typeof(SamplePinnedCmd), "Sample.Perspective, Sample", "perspective", null),
    ]);

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows.Count(r => r.PinnedId is null)).IsEqualTo(2);
    await Assert.That(rows.Count(r => r.PinnedId is not null)).IsEqualTo(1);
  }

  [Test]
  public async Task PopulateAsync_SummaryLog_IncludesPinnedAndUnpinnedCountsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned1, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned2, Sample", "command", "22222222-2222-2222-2222-222222222222"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Unpinned1, Sample", "event", null),
    ]);
    var logger = new CapturingLogger<EFCoreMessageTypeRegistryPopulator>();

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource, logger);
    await populator.PopulateAsync();

    var summary = logger.Entries.FirstOrDefault(e => e.Message.Contains("Message type registry populated", StringComparison.Ordinal));
    await Assert.That(summary).IsNotNull();
    await Assert.That(summary!.Message).Contains("3 entries");
    await Assert.That(summary!.Message).Contains("2 pinned");
    await Assert.That(summary!.Message).Contains("1 unpinned");
  }

  [Test]
  public async Task PopulateAsync_EmptyCatalog_SkipsWithLogAndLeavesRegistryUntouchedAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var logger = new CapturingLogger<EFCoreMessageTypeRegistryPopulator>();

    var populator = new EFCoreMessageTypeRegistryPopulator(new FakeCatalog([]), dataSource, logger);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).IsEmpty();
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("Message type catalog is empty", StringComparison.Ordinal))).IsTrue()
      .Because("The empty-catalog branch must log the skip and return without touching the database.");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("Message type registry populated", StringComparison.Ordinal))).IsFalse()
      .Because("The summary log must not fire when population was skipped.");
  }

  [Test]
  public async Task PopulateAsync_SpecialCharactersInClrTypeName_RoundTripThroughJsonEscapingAsync() {
    // Exercises the hand-rolled JSON escaping (_jsonString): quote, backslash,
    // \b \f \n \r \t and a raw control character below 0x20.
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    const string weirdName = "A\"B\\C\bD\fE\nF\rG\tH\u0001I, Sample";
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), weirdName, "event", null),
    ]);

    var populator = new EFCoreMessageTypeRegistryPopulator(catalog, dataSource);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo(weirdName)
      .Because("Every escaped character must survive the JSONB round trip byte-for-byte.");
  }

  private async Task<List<RegistryRow>> _queryRegistryAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT type_id, clr_type_name, pinned_id, kind, updated_at FROM wh_message_type_registry ORDER BY clr_type_name";
    var rows = new List<RegistryRow>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new RegistryRow(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4)));
    }
    return rows;
  }

  private sealed record RegistryRow(Guid TypeId, string ClrTypeName, Guid? PinnedId, string Kind, DateTimeOffset UpdatedAt);

  private sealed class FakeCatalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed record LogEntry(LogLevel Level, string Message);

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed record SamplePinned;
  private sealed record SamplePinnedCmd;
}
