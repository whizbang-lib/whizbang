#pragma warning disable CA1707, CA1859 // tests assert against the interface return type

using System.Linq.Expressions;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// End-to-end integration coverage for the Dapper collective apply path against a real Postgres
/// testcontainer — the Dapper counterpart of <c>CollectiveDispatcherEFCoreIntegrationTests</c>. Proves
/// the resolver's scope filter is the SOLE predicate: a tenant-A event archives every tenant-A row and
/// leaves tenant-B rows untouched, via a single <c>UPDATE … SET data = jsonb_set(...) WHERE scope-&gt;&gt;…</c>.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperCollectiveApplierIntegrationTests : PostgresTestBase {

  private const string TABLE = "wh_per_collective_dapper";
  private const string STATUS_TABLE = "wh_per_collective_dapper_status";

  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  private async Task _createTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb,
        scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {TABLE};");
  }

  private async Task _seedAsync(Guid id, string tenantId, string status) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, @data::jsonb, @scope::jsonb)",
      new { id, data = $"{{\"Status\": \"{status}\"}}", scope = $"{{\"t\": \"{tenantId}\"}}" });
  }

  private async Task<string?> _statusAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.ExecuteScalarAsync<string?>(
      $"SELECT data->>'Status' FROM {TABLE} WHERE id = @id", new { id });
  }

  // ── Apply hooks (collective path, Dapper) ─────────────────────────────

  private async Task<(DateTime UpdatedAt, long Version)> _updatedAtVersionAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var updatedAt = await conn.ExecuteScalarAsync<DateTime>($"SELECT updated_at FROM {TABLE} WHERE id = @id", new { id });
    var version = await conn.ExecuteScalarAsync<long>($"SELECT version FROM {TABLE} WHERE id = @id", new { id });
    return (updatedAt, version);
  }

  private static CollectiveApplyEntry _jobEntry() => new(
    ModelType: typeof(JobModel), EventType: typeof(ArchiveEvent), HandlerType: typeof(JobPerspective),
    MethodName: nameof(JobPerspective.Archive), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, _) => ((JobPerspective)h).Archive((ArchiveEvent)e));

  private Task<int> _applyWithHooksAsync(CollectiveApplyHookRegistry hooks, string tenant = "t-A") =>
    DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _jobEntry(), new JobPerspective(), new ArchiveEvent { Scope = new TenantCollectiveScope(tenant) },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default,
      logger: null, hookRegistry: hooks);

  private sealed class CollectiveHook<TMarker>(Action<ICollectiveApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : ICollectiveApplyHook<TMarker> {
    public void Configure(ICollectiveApplyHookBuilder<TMarker> builder, ApplyHookContext context) => body(builder, context);
  }

  [Test]
  public async Task Hook_SetProperty_OverridesSpecField_AndLastRegisteredWinsAsync() {
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-A", "Active");

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<JobModel>(new CollectiveHook<JobModel>((b, _) => b.SetProperty(j => j.Status, "first")))
      .Register<JobModel>(new CollectiveHook<JobModel>((b, _) => b.SetProperty(j => j.Status, "second")));

    await _applyWithHooksAsync(hooks);

    await Assert.That(await _statusAsync(job)).IsEqualTo("second")
      .Because("Hook SetProperty is appended after the spec setter and the last-registered hook wins.");
  }

  [Test]
  public async Task Hook_SetColumn_OverridesDefaultUpdatedAt_AndSkipsVersionBumpAsync() {
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-A", "Active");
    var sentinel = new DateTimeOffset(2099, 3, 4, 5, 6, 7, TimeSpan.Zero);

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<object>(new CollectiveHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, sentinel)),
        key: WhizbangApplyHookKeys.TIMESTAMPS);

    await _applyWithHooksAsync(hooks);

    var (updatedAt, version) = await _updatedAtVersionAsync(job);
    await Assert.That(updatedAt.Year).IsEqualTo(2099)
      .Because("Re-registering whizbang.timestamps replaces the default stamp — updated_at is the sentinel.");
    await Assert.That(version).IsEqualTo(1L)
      .Because("The override sets updated_at but does not BumpVersion, so version stays 1.");
  }

  [Test]
  public async Task Hook_RemoveSetter_DropsASpecFieldSetterAsync() {
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-A", "Active");

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<JobModel>(new CollectiveHook<JobModel>((b, _) => b.RemoveSetter(j => j.Status)));

    await _applyWithHooksAsync(hooks);

    await Assert.That(await _statusAsync(job)).IsEqualTo("Active")
      .Because("RemoveSetter(Status) drops the spec's Status=\"Archived\" setter — Status stays its seeded value.");
  }

  [Test]
  public async Task Hook_AndWhere_RefinesTheCohortAsync() {
    await _createTableAsync();
    var active = Guid.NewGuid();
    var draft = Guid.NewGuid();
    await _seedAsync(active, "t-A", "Active");
    await _seedAsync(draft, "t-A", "Draft");

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<JobModel>(new CollectiveHook<JobModel>((b, _) => b.AndWhere(j => j.Status == "Active")));

    var affected = await _applyWithHooksAsync(hooks);

    await Assert.That(affected).IsEqualTo(1)
      .Because("AndWhere(Status==\"Active\") narrows the scope cohort to just the Active row.");
    await Assert.That(await _statusAsync(active)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(draft)).IsEqualTo("Draft")
      .Because("The Draft row falls out of the hook-refined cohort — untouched.");
  }

  [Test]
  public async Task Hook_ReplaceWhere_SwapsCohortButScopeStillBindsAsync() {
    await _createTableAsync();
    var activeA = Guid.NewGuid();
    var draftA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    await _seedAsync(activeA, "t-A", "Active");
    await _seedAsync(draftA, "t-A", "Draft");
    await _seedAsync(draftB, "t-B", "Draft");

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<JobModel>(new CollectiveHook<JobModel>((b, _) => b.ReplaceWhere(j => j.Status == "Draft")));

    var affected = await _applyWithHooksAsync(hooks);

    await Assert.That(affected).IsEqualTo(1)
      .Because("ReplaceWhere(Status==\"Draft\") swaps the cohort, but the tenant scope still binds.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(activeA)).IsEqualTo("Active");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Draft")
      .Because("D0: the scope envelope still binds under ReplaceWhere — tenant B is untouched.");
  }

  [Test]
  public async Task Hook_NonMatchingMarker_IsNotAppliedAsync() {
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-A", "Active");
    var sentinel = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Gated on StatusModel — a class JobModel is NOT assignable to; registered UNKEYED so the default stays.
    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<StatusModel>(new CollectiveHook<StatusModel>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, sentinel)));

    await _applyWithHooksAsync(hooks);

    var (updatedAt, version) = await _updatedAtVersionAsync(job);
    await Assert.That(updatedAt.Year).IsNotEqualTo(1999)
      .Because("The _unrelatedMarker hook does not match JobModel, so its sentinel stamp never applies.");
    await Assert.That(version).IsEqualTo(2L)
      .Because("Only the default hook fired — it bumped version 1 → 2.");
  }

  private async Task _createStatusTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {STATUS_TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb,
        scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {STATUS_TABLE};");
  }

  private async Task _seedStatusAsync(Guid id, string status) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {STATUS_TABLE} (id, data, scope) VALUES (@id, @data::jsonb, '{{}}'::jsonb)",
      new { id, data = $"{{\"Status\": \"{status}\"}}" });
  }

  [Test]
  public async Task ApplyAsync_CrossPerspectiveCohort_ScopesBySiblingTableAsync() {
    // The cohort is defined by a status that lives on a SIBLING table (same id). The handler's Where uses
    // q.Of<StatusModel>().Any(...), which the Dapper compiler turns into a correlated EXISTS over the
    // sibling table — proving cross-perspective projection end-to-end on the Dapper driver.
    await _createTableAsync();
    await _createStatusTableAsync();

    var eligible = Guid.NewGuid();   // sibling status Draft → in cohort
    var ineligible = Guid.NewGuid(); // sibling status Published → out
    var noSibling = Guid.NewGuid();  // no sibling row → out

    await _seedAsync(eligible, "t-A", "Active");
    await _seedAsync(ineligible, "t-A", "Active");
    await _seedAsync(noSibling, "t-A", "Active");
    await _seedStatusAsync(eligible, "Draft");
    await _seedStatusAsync(ineligible, "Published");

    var siblings = new Dictionary<Type, string> { [typeof(StatusModel)] = STATUS_TABLE };

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _crossEntry(),
      new CrossPerspective(),
      new ArchiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      siblings, CollectiveApplyOptions.Default,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Only the job whose sibling status is Draft is in the cohort (correlated EXISTS over the status table).");
    await Assert.That(await _statusAsync(eligible)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(ineligible)).IsEqualTo("Active")
      .Because("Sibling status Published is not in the eligible set.");
    await Assert.That(await _statusAsync(noSibling)).IsEqualTo("Active")
      .Because("No sibling row → the EXISTS correlation finds nothing.");
  }

  private static CollectiveApplyEntry _crossEntry() => new(
    ModelType: typeof(JobModel),
    EventType: typeof(ArchiveEvent),
    HandlerType: typeof(CrossPerspective),
    MethodName: nameof(CrossPerspective.Archive),
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((CrossPerspective)h).Archive((ArchiveEvent)e, q));

  [SuppressIndexAdvisory("test fixture; the table holds a handful of rows")]
  private sealed class StatusModel {
    public string Status { get; set; } = "";
  }

  // Cross-perspective handler: scopes the mutated job table by a status on the SIBLING status table.
  private sealed class CrossPerspective {
    private static readonly string[] _eligible = ["Draft"];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Archive(ArchiveEvent e, ICollectiveQuery q) =>
      new WhereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => q.Of<StatusModel>().Any(st => st.Id == r.Id && _eligible.Contains(st.Data.Status)));

    private sealed record WhereSpec(
        Expression<Action<ICollectiveSetters<JobModel>>> Setters,
        Expression<Func<PerspectiveRow<JobModel>, bool>>? Where) : ICollectiveSpec<JobModel>;
  }

  [Test]
  public async Task ApplyAsync_TenantScoped_UpdatesOnlyInScopeRowsAsync() {
    await _createTableAsync();
    var a1 = Guid.NewGuid();
    var a2 = Guid.NewGuid();
    var b1 = Guid.NewGuid();
    await _seedAsync(a1, "t-A", "Active");
    await _seedAsync(a2, "t-A", "Active");
    await _seedAsync(b1, "t-B", "Active");

    var handler = new JobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(JobModel),
      EventType: typeof(ArchiveEvent),
      HandlerType: typeof(JobPerspective),
      MethodName: nameof(JobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, _) => ((JobPerspective)h).Archive((ArchiveEvent)e));

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      entry,
      handler,
      new ArchiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings, CollectiveApplyOptions.Default,
      default);

    await Assert.That(affected).IsEqualTo(2)
      .Because("Exactly the two tenant-A rows are in scope.");
    await Assert.That(await _statusAsync(a1)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(a2)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(b1)).IsEqualTo("Active")
      .Because("The resolver scope filter is the SOLE WHERE — tenant-B rows are entirely untouched.");
  }

  [Test]
  public async Task ApplyAsync_BumpsStoreManagedUpdatedAtAndVersionAsync() {
    // Parity with EFCoreCollectiveAdapter: a collective UPDATE must stamp updated_at = now and version = version+1,
    // not leave them stale — otherwise change-detection (delta sync, downstream mirrors) misses the flip.
    await _createTableAsync();
    var id = Guid.NewGuid();
    var stale = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    using (var seedConn = await ConnectionFactory.CreateConnectionAsync()) {
      await seedConn.ExecuteAsync(
        $"INSERT INTO {TABLE} (id, data, scope, updated_at, version) VALUES (@id, @data::jsonb, @scope::jsonb, @updatedAt, 1)",
        new { id, data = "{\"Status\": \"Active\"}", scope = "{\"t\": \"t-stamp\"}", updatedAt = stale });
    }

    var handler = new JobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(JobModel),
      EventType: typeof(ArchiveEvent),
      HandlerType: typeof(JobPerspective),
      MethodName: nameof(JobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, _) => ((JobPerspective)h).Archive((ArchiveEvent)e));

    var before = DateTime.UtcNow;
    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      entry, handler, new ArchiveEvent { Scope = new TenantCollectiveScope("t-stamp") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default, default);
    await Assert.That(affected).IsEqualTo(1);

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var updatedAt = await conn.ExecuteScalarAsync<DateTime>($"SELECT updated_at FROM {TABLE} WHERE id = @id", new { id });
    var version = await conn.ExecuteScalarAsync<long>($"SELECT version FROM {TABLE} WHERE id = @id", new { id });
    await Assert.That(version).IsEqualTo(2L)
      .Because("A collective UPDATE must bump the store-managed version, like a per-event apply.");
    await Assert.That(updatedAt).IsGreaterThanOrEqualTo(before)
      .Because("A collective UPDATE must stamp updated_at = now, not leave the stale 2020 seed.");
    await Assert.That(updatedAt.Year).IsNotEqualTo(2020);
  }

  [Test]
  public async Task ApplyAsync_CohortLargerThanBatchSize_UpdatesEveryRowAcrossBatchesAsync() {
    // §4 parity: a cohort bigger than BatchSize is applied via the keyset loop (SELECT id … LIMIT + UPDATE …
    // WHERE id = ANY, looped on id > cursor). Every row must be updated exactly once — the loop covers the
    // whole cohort and terminates (no gaps, no repeats, no infinite loop). Runs with the exclusive advisory
    // lock on (default SerializeApplies=true), so this also exercises the §5a lock path end-to-end.
    await _createTableAsync();
    var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
    foreach (var id in ids) {
      await _seedAsync(id, "t-A", "Active");
    }

    var handler = new JobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(JobModel),
      EventType: typeof(ArchiveEvent),
      HandlerType: typeof(JobPerspective),
      MethodName: nameof(JobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, _) => ((JobPerspective)h).Archive((ArchiveEvent)e));

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      entry,
      handler,
      new ArchiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings, new CollectiveApplyOptions { BatchSize = 2 },
      logger: null,
      default);

    await Assert.That(affected).IsEqualTo(5)
      .Because("All 5 rows are applied across ⌈5/2⌉ = 3 keyset batches — the total is the sum of the batch counts.");
    foreach (var id in ids) {
      await Assert.That(await _statusAsync(id)).IsEqualTo("Archived")
        .Because("Every row in the cohort is updated exactly once across the batches.");
    }
  }

  [Test]
  public async Task ApplyAsync_FrameworkWithHandlerWhere_RefinesWithinScopeAsync() {
    await _createTableAsync();
    var draftA = Guid.NewGuid();
    var approvedA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    await _seedAsync(draftA, "t-A", "Draft");
    await _seedAsync(approvedA, "t-A", "Approved");
    await _seedAsync(draftB, "t-B", "Draft");

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Framework),
      new DraftPerspective(),
      new ArchiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings, CollectiveApplyOptions.Default,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Framework AND-composes the tenant envelope (scope->>'TenantId') with the handler's data->>'Status'='Draft' — only tenant-A's Draft row.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(approvedA)).IsEqualTo("Approved")
      .Because("Handler Where refines within scope — the Approved row in the same tenant falls out.");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Draft")
      .Because("Scope envelope still binds — a Draft row in tenant B is excluded.");
  }

  [Test]
  public async Task ApplyAsync_CustomHandlerWhere_StillHonorsTenantScopeAsync() {
    // D0 safety fix: under Custom the handler owns the cohort predicate, but the framework STILL ANDs the
    // tenant envelope on shared multi-tenant tables — so a tenant-A event never touches tenant-B rows.
    await _createTableAsync();
    var draftA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    var approvedB = Guid.NewGuid();
    await _seedAsync(draftA, "t-A", "Draft");
    await _seedAsync(draftB, "t-B", "Draft");
    await _seedAsync(approvedB, "t-B", "Approved");

    var affected = await DapperCollectiveEventApplier<JobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Custom),
      new DraftPerspective(),
      new ArchiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings, CollectiveApplyOptions.Default,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Custom refines the cohort (Status=='Draft') but the framework still ANDs the tenant envelope — only tenant-A's single Draft row qualifies.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Draft")
      .Because("D0 FIX: tenant-B Draft row is UNTOUCHED even under Custom — the scope envelope always binds.");
    await Assert.That(await _statusAsync(approvedB)).IsEqualTo("Approved")
      .Because("Handler Where (Status=='Draft') excludes the Approved row anyway.");
  }

  private static CollectiveApplyEntry _draftsEntry(CollectiveScopeHandling handling) => new(
    ModelType: typeof(JobModel),
    EventType: typeof(ArchiveEvent),
    HandlerType: typeof(DraftPerspective),
    MethodName: nameof(DraftPerspective.ArchiveDrafts),
    ScopeHandling: handling,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, _) => ((DraftPerspective)h).ArchiveDrafts((ArchiveEvent)e));

  // ── Keyed-array element upsert (Dapper) ─────────────────────────────

  private async Task _seedCellsAsync(Guid id, string tenantId, string dataJson) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, @data::jsonb, @scope::jsonb)",
      new { id, data = dataJson, scope = $"{{\"t\": \"{tenantId}\"}}" });
  }

  private async Task<List<string>> _cellsAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var json = await conn.ExecuteScalarAsync<string>(
      $"SELECT COALESCE(data->'Cells', '[]'::jsonb)::text FROM {TABLE} WHERE id = @id", new { id });
    using var doc = System.Text.Json.JsonDocument.Parse(json!);
    return [.. doc.RootElement.EnumerateArray().Select(e => $"{e.GetProperty("Key").GetString()}={e.GetProperty("Value").GetString()}")];
  }

  private Task<int> _upsertCellAsync(string tenant, string key, string value, string? tag = null) =>
    DapperCollectiveEventApplier<CellsModel>.ApplyAsync(
      new CollectiveApplyEntry(
        ModelType: typeof(CellsModel), EventType: typeof(UpsertCellEvent), HandlerType: typeof(CellsPerspective),
        MethodName: nameof(CellsPerspective.Upsert), ScopeHandling: CollectiveScopeHandling.Framework,
        SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, _) => ((CellsPerspective)h).Upsert((UpsertCellEvent)e)),
      new CellsPerspective(),
      new UpsertCellEvent { Scope = new TenantCollectiveScope(tenant), Key = key, Value = value, Tag = tag },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default);

  private const string TWO_CELLS = """{"Tag":"t","Cells":[{"Key":"k1","Value":"v1"},{"Key":"k2","Value":"v2"}]}""";

  [Test]
  public async Task UpsertElement_ReplacesTheMatchingElement_KeepingOrder_WithinScopeAsync() {
    await _createTableAsync();
    var id = Guid.NewGuid();
    var other = Guid.NewGuid();
    await _seedCellsAsync(id, "t-A", TWO_CELLS);
    await _seedCellsAsync(other, "t-B", TWO_CELLS);

    await _upsertCellAsync("t-A", "k1", "new");

    await Assert.That(await _cellsAsync(id)).IsEquivalentTo(["k1=new", "k2=v2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _cellsAsync(other)).IsEquivalentTo(["k1=v1", "k2=v2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task UpsertElement_AppendsWhenNoElementHasTheKeyAsync() {
    await _createTableAsync();
    var id = Guid.NewGuid();
    await _seedCellsAsync(id, "t-A", TWO_CELLS);

    await _upsertCellAsync("t-A", "k3", "v3");

    await Assert.That(await _cellsAsync(id)).IsEquivalentTo(["k1=v1", "k2=v2", "k3=v3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  [Arguments("""{"Tag":"t"}""")]
  [Arguments("""{"Tag":"t","Cells":null}""")]
  public async Task UpsertElement_OnAMissingArray_WritesAOneElementArrayAsync(string dataJson) {
    await _createTableAsync();
    var id = Guid.NewGuid();
    await _seedCellsAsync(id, "t-A", dataJson);

    await _upsertCellAsync("t-A", "k1", "v1");

    await Assert.That(await _cellsAsync(id)).IsEquivalentTo(["k1=v1"]);
  }

  [Test]
  public async Task UpsertElement_ComposesWithSetPropertyAsync() {
    await _createTableAsync();
    var id = Guid.NewGuid();
    await _seedCellsAsync(id, "t-A", TWO_CELLS);

    await _upsertCellAsync("t-A", "k2", "new", tag: "after");

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await Assert.That(await conn.ExecuteScalarAsync<string>($"SELECT data->>'Tag' FROM {TABLE} WHERE id = @id", new { id })).IsEqualTo("after");
    await Assert.That(await _cellsAsync(id)).IsEquivalentTo(["k1=v1", "k2=new"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  private sealed class CellsModel {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "The document shape the collective writes; the setter is exercised by the UPDATE, not by C#.")]
    public string? Tag { get; set; }
    public List<Cell> Cells { get; set; } = [];
  }

  private sealed class Cell {
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
  }

  private sealed class CellsPerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<CellsModel> Upsert(UpsertCellEvent e) {
      var cell = new Cell { Key = e.Key, Value = e.Value };
      return e.Tag is null
        ? new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell))
        : new Spec(s => s.SetProperty(m => m.Tag, e.Tag).UpsertElement(m => m.Cells, c => c.Key, cell));
    }

    private sealed record Spec(Expression<Action<ICollectiveSetters<CellsModel>>> Setters)
      : ICollectiveSpec<CellsModel>;
  }

  private sealed record UpsertCellEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? Tag { get; init; }
  }

  private sealed class JobModel {
    public string Status { get; set; } = "";
  }

  // Projects the cohort onto its own data column via spec.Where — the per-perspective projection capability.
  private sealed class DraftPerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> ArchiveDrafts(ArchiveEvent e) =>
      new WhereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => r.Data.Status == "Draft");

    private sealed record WhereSpec(
        Expression<Action<ICollectiveSetters<JobModel>>> Setters,
        Expression<Func<PerspectiveRow<JobModel>, bool>>? Where) : ICollectiveSpec<JobModel>;
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

  // ── Computed comparison setter: single-active flip (the overlay-activation shape) ──────────────────────

  private const string OVERLAY_TABLE = "wh_per_collective_dapper_overlay";

  private async Task _createOverlayTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {OVERLAY_TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {OVERLAY_TABLE};");
  }

  private async Task _seedOverlayAsync(Guid id, string tenantId, bool isActive, Guid globalTemplateId) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {OVERLAY_TABLE} (id, data, scope) VALUES (@id, @data::jsonb, @scope::jsonb)",
      new {
        id,
        data = $"{{\"Id\": \"{id}\", \"IsActive\": {(isActive ? "true" : "false")}, \"GlobalTemplateId\": \"{globalTemplateId}\"}}",
        scope = $"{{\"t\": \"{tenantId}\"}}"
      });
  }

  private async Task<bool> _isActiveAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.ExecuteScalarAsync<bool>($"SELECT (data->>'IsActive')::bool FROM {OVERLAY_TABLE} WHERE id = @id", new { id });
  }

  [Test]
  public async Task ApplyAsync_ComputedComparisonSetter_FlipsSingleActive_AtomicallyAsync() {
    // The overlay-activation shape: SET IsActive = (Id == @target) for every sibling under one global template.
    // One computed set-based UPDATE activates the target AND deactivates its siblings — no read-before-write.
    await _createOverlayTableAsync();
    var gid = Guid.NewGuid();
    var otherGid = Guid.NewGuid();
    var target = Guid.NewGuid();     // becomes active
    var sibling = Guid.NewGuid();    // currently active → must be deactivated
    var unrelated = Guid.NewGuid();  // different global template → untouched

    await _seedOverlayAsync(target, "t-A", isActive: false, gid);
    await _seedOverlayAsync(sibling, "t-A", isActive: true, gid);
    await _seedOverlayAsync(unrelated, "t-A", isActive: true, otherGid);

    var affected = await DapperCollectiveEventApplier<OverlayModel>.ApplyAsync(
      _setActiveEntry(),
      new OverlayActivePerspective(),
      new SetActiveEvent { OverlayId = target, GlobalTemplateId = gid, Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      OVERLAY_TABLE,
      _noSiblings, CollectiveApplyOptions.Default,
      default);

    await Assert.That(affected).IsEqualTo(2)
      .Because("Both siblings under the global template are updated (target set active, sibling set inactive).");
    await Assert.That(await _isActiveAsync(target)).IsTrue()
      .Because("The computed setter sets IsActive = (Id == target) → true for the target.");
    await Assert.That(await _isActiveAsync(sibling)).IsFalse()
      .Because("Same computed setter → false for the sibling (Id != target), atomically deactivating the prior active.");
    await Assert.That(await _isActiveAsync(unrelated)).IsTrue()
      .Because("A different global template is outside the cohort (Where GlobalTemplateId == gid) → untouched.");
  }

  private static CollectiveApplyEntry _setActiveEntry() => new(
    ModelType: typeof(OverlayModel),
    EventType: typeof(SetActiveEvent),
    HandlerType: typeof(OverlayActivePerspective),
    MethodName: nameof(OverlayActivePerspective.SetActive),
    ScopeHandling: CollectiveScopeHandling.Custom,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, _) => ((OverlayActivePerspective)h).SetActive((SetActiveEvent)e));

  private sealed class OverlayModel {
    public Guid Id { get; }
    public bool IsActive { get; }
    public Guid GlobalTemplateId { get; }
  }

  private sealed class OverlayActivePerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<OverlayModel> SetActive(SetActiveEvent e) =>
      new WhereSpec(
        s => s.SetProperty(o => o.IsActive, o => o.Id == e.OverlayId),
        r => r.Data.GlobalTemplateId == e.GlobalTemplateId);

    private sealed record WhereSpec(
        Expression<Action<ICollectiveSetters<OverlayModel>>> Setters,
        Expression<Func<PerspectiveRow<OverlayModel>, bool>>? Where) : ICollectiveSpec<OverlayModel>;
  }

  private sealed record SetActiveEvent : ICollectiveEvent {
    public required Guid OverlayId { get; init; }
    public required Guid GlobalTemplateId { get; init; }
    public required CollectiveScope Scope { get; init; }
  }
}
