// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Dapper.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.BranchCoverage.DapperDriver;

/// <summary>
/// Branch outcomes of <see cref="DapperPostgresPerspectiveStore{TModel}"/> that need a real table: a
/// null scope on the explicit-scope physical-field overloads, a per-event hook that clears the
/// <c>updated_at</c> value, and collection values bound to a plain physical column.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperPerspectiveStoreBranchCoverageTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_branch_coverage";

  private static readonly JsonSerializerOptions _storeOptions = new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      DapperPerspectiveTestJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private static readonly JsonSerializerOptions _describingOptions = new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      DapperPerspectiveTestJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default,
      new DefaultJsonTypeInfoResolver()),
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  [Before(Test)]
  public async Task CreatePerspectiveTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync($$"""
      CREATE TABLE IF NOT EXISTS {{TABLE_NAME}} (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
        scope JSONB NOT NULL DEFAULT '{}'::jsonb,
        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        version INT NOT NULL DEFAULT 1,
        tags_text TEXT NULL)
      """);
  }

  /// <summary>
  /// The force-scope physical-field overload given no scope writes the row with an empty scope
  /// rather than failing or leaving the scope column unset.
  /// </summary>
  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_ForceOverloadNullScope_WritesAnEmptyScopeAsync() {
    var store = _store(_storeOptions);
    var id = Guid.CreateVersion7();

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "no-scope" },
      new Dictionary<string, object?>(), scope: null, forceUpdateScope: true);

    var row = await _readAsync(id);
    await Assert.That(row.Name).IsEqualTo("no-scope");
    await Assert.That(row.TenantId).IsNull();
  }

  /// <summary>The metadata-bearing physical-field overload given no scope still persists the metadata.</summary>
  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_MetadataOverloadNullScope_PersistsTheMetadataAsync() {
    var store = _store(_storeOptions);
    var id = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "CoverageOccurred",
      EventId = Guid.CreateVersion7().ToString(),
      Timestamp = DateTime.UtcNow
    };

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "with-metadata" },
      new Dictionary<string, object?>(), scope: null, forceUpdateScope: false, metadata);

    var row = await _readAsync(id);
    await Assert.That(row.EventType).IsEqualTo("CoverageOccurred");
    await Assert.That(row.TenantId).IsNull();
  }

  /// <summary>
  /// A per-event hook that clears <c>updated_at</c> leaves the write site's own clock in charge: the
  /// row is written with the time of the write, never with a null the column cannot hold.
  /// </summary>
  [Test]
  public async Task Upsert_HookClearsUpdatedAt_StampsTheWriteTimeAsync() {
    var original = PerEventApplyHooks.Registry;
    PerEventApplyHooks.Registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<DapperPostgresPerspectiveStoreTests.TestModel>(new ClearUpdatedAtHook());
    try {
      var store = _store(_storeOptions);
      var id = Guid.CreateVersion7();
      var before = DateTime.UtcNow.AddSeconds(-1);

      await store.UpsertAsync(id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "hooked" }, new PerspectiveScope());

      var after = DateTime.UtcNow.AddSeconds(1);
      var row = await _readAsync(id);
      await Assert.That(row.UpdatedAt).IsGreaterThanOrEqualTo(before);
      await Assert.That(row.UpdatedAt).IsLessThanOrEqualTo(after);
    } finally {
      PerEventApplyHooks.Registry = original;
    }
  }

  /// <summary>
  /// A collection the JSON options can describe, bound to a plain (non-jsonb) physical column, is
  /// sent as its JSON text for the column to hold.
  /// </summary>
  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_DescribableCollection_IsSentAsJsonTextAsync() {
    var store = _store(_describingOptions);
    var id = Guid.CreateVersion7();

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "tags" },
      new Dictionary<string, object?> { ["tags_text"] = new List<string> { "a", "b" } },
      new PerspectiveScope());

    var row = await _readAsync(id);
    await Assert.That(row.TagsText).IsEqualTo("[\"a\",\"b\"]");
  }

  /// <summary>
  /// A collection the JSON options cannot describe falls back to its own text form, the same as any
  /// other value a column type parses.
  /// </summary>
  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_UndescribableCollection_IsSentAsItsTextFormAsync() {
    var store = _store(_storeOptions);
    var id = Guid.CreateVersion7();

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "opaque" },
      new Dictionary<string, object?> { ["tags_text"] = new PipeJoinedTags { "a", "b" } },
      new PerspectiveScope());

    var row = await _readAsync(id);
    await Assert.That(row.TagsText).IsEqualTo("a|b");
  }

  private DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel> _store(JsonSerializerOptions options) =>
    new(ConnectionString, TABLE_NAME, options);

  private async Task<StoredRow> _readAsync(Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return await conn.QuerySingleAsync<StoredRow>(
      $"""
      SELECT data->>'Name' AS Name, scope->>'t' AS TenantId, metadata->>'EventType' AS EventType,
             updated_at AS UpdatedAt, tags_text AS TagsText
      FROM {TABLE_NAME} WHERE id = @id
      """,
      new { id });
  }

  private sealed record StoredRow(string? Name, string? TenantId, string? EventType, DateTime UpdatedAt, string? TagsText);
}

/// <summary>
/// Branch outcomes of <see cref="DapperCollectiveEventApplier{TModel}"/> that need a real table:
/// a non-positive batch size, unserialized applies, an unbounded or disabled lock wait, and an
/// apply-hook store column set to null.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperCollectiveApplierBranchCoverageTests : PostgresTestBase {
  private const string TABLE = "wh_per_collective_dapper_branch";

  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  [Before(Test)]
  public async Task CreateCollectiveTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb,
        scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1,
        audit_note text NULL DEFAULT 'seeded');
      TRUNCATE {TABLE};");
  }

  /// <summary>A batch size of zero means "use the default", not "select nothing": the cohort is still applied.</summary>
  [Test]
  public async Task ApplyAsync_ZeroBatchSize_FallsBackToTheDefaultBatchAsync() {
    var job = await _seedAsync("t-batch");

    var affected = await _applyAsync("t-batch", CollectiveApplyOptions.Default with { BatchSize = 0 });

    await Assert.That(affected).IsEqualTo(1);
    await Assert.That(await _statusAsync(job)).IsEqualTo("Archived");
  }

  /// <summary>With serialization turned off the apply takes no advisory lock and still applies the cohort.</summary>
  [Test]
  public async Task ApplyAsync_SerializationOff_AppliesWithoutTheLockAsync() {
    var job = await _seedAsync("t-unserialized");

    var affected = await _applyAsync("t-unserialized", CollectiveApplyOptions.Default with { SerializeApplies = false });

    await Assert.That(affected).IsEqualTo(1);
    await Assert.That(await _statusAsync(job)).IsEqualTo("Archived");
  }

  /// <summary>
  /// A lock wait that is unset or zero sets no lock timeout: the lock is still taken and the cohort
  /// applied, waiting as long as the statement allows.
  /// </summary>
  [Test]
  public async Task ApplyAsync_NoLockWaitBound_StillTakesTheLockAndAppliesAsync() {
    var unsetJob = await _seedAsync("t-wait-unset");
    var zeroJob = await _seedAsync("t-wait-zero");

    var unset = await _applyAsync("t-wait-unset", CollectiveApplyOptions.Default with { LockWaitSeconds = null });
    var zero = await _applyAsync("t-wait-zero", CollectiveApplyOptions.Default with { LockWaitSeconds = 0 });

    await Assert.That(unset).IsEqualTo(1);
    await Assert.That(zero).IsEqualTo(1);
    await Assert.That(await _statusAsync(unsetJob)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(zeroJob)).IsEqualTo("Archived");
  }

  /// <summary>An apply hook that sets a store column to null writes NULL into that column.</summary>
  [Test]
  public async Task ApplyAsync_HookSetsAStoreColumnToNull_WritesNullAsync() {
    var job = await _seedAsync("t-null-column");
    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<JobModel>(new NullColumnHook());

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _entry(), new JobPerspective(), new ArchiveEvent { Scope = new TenantCollectiveScope("t-null-column") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default,
      logger: null, hookRegistry: hooks);

    await Assert.That(affected).IsEqualTo(1);
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var note = await conn.ExecuteScalarAsync<string?>($"SELECT audit_note FROM {TABLE} WHERE id = @job", new { job });
    await Assert.That(note).IsNull();
  }

  private Task<int> _applyAsync(string tenant, CollectiveApplyOptions options) =>
    DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _entry(), new JobPerspective(), new ArchiveEvent { Scope = new TenantCollectiveScope(tenant) },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, options);

  private async Task<Guid> _seedAsync(string tenantId) {
    var id = Guid.CreateVersion7();
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, @data::jsonb, @scope::jsonb)",
      new { id, data = "{\"Status\": \"Active\"}", scope = $"{{\"t\": \"{tenantId}\"}}" });
    return id;
  }

  private async Task<string?> _statusAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.ExecuteScalarAsync<string?>($"SELECT data->>'Status' FROM {TABLE} WHERE id = @id", new { id });
  }

  private static CollectiveApplyEntry _entry() => new(
    ModelType: typeof(JobModel),
    EventType: typeof(ArchiveEvent),
    HandlerType: typeof(JobPerspective),
    MethodName: nameof(JobPerspective.Archive),
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, _) => ((JobPerspective)h).Archive((ArchiveEvent)e));

  private sealed class JobModel {
    public string Status { get; set; } = "";
  }

  private sealed class JobPerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Archive(ArchiveEvent e) =>
      new Spec(s => s.SetProperty(j => j.Status, "Archived"));

    private sealed record Spec(Expression<Action<ICollectiveSetters<JobModel>>> Setters)
      : ICollectiveSpec<JobModel>;
  }

  private sealed record ArchiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class NullColumnHook : ICollectiveApplyHook<JobModel> {
    public void Configure(ICollectiveApplyHookBuilder<JobModel> builder, ApplyHookContext context) =>
      builder.SetColumn("audit_note", null);
  }
}

/// <summary>A per-event hook that clears the <c>updated_at</c> value the default hook set.</summary>
sealed file class ClearUpdatedAtHook : IApplyHook<DapperPostgresPerspectiveStoreTests.TestModel> {
  public void Configure(IApplyHookBuilder<DapperPostgresPerspectiveStoreTests.TestModel> b, ApplyHookContext context) =>
    b.SetColumn(ApplyHookColumns.UPDATED_AT, null);
}

/// <summary>A collection type no JSON context describes, whose text form is its items joined by pipes.</summary>
sealed file class PipeJoinedTags : List<string> {
  public override string ToString() => string.Join('|', this);
}
