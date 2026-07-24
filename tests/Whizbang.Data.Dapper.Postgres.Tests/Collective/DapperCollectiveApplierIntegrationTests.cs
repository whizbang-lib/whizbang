#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type

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
    ModelType: typeof(_jobModel), EventType: typeof(_archiveEvent), HandlerType: typeof(_jobPerspective),
    MethodName: nameof(_jobPerspective.Archive), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

  private Task<int> _applyWithHooksAsync(CollectiveApplyHookRegistry hooks, string tenant = "t-A") =>
    DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _jobEntry(), new _jobPerspective(), new _archiveEvent { Scope = new TenantCollectiveScope(tenant) },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default,
      logger: null, hookRegistry: hooks);

  private sealed class _collectiveHook<TMarker>(Action<ICollectiveApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : ICollectiveApplyHook<TMarker> {
    public void Configure(ICollectiveApplyHookBuilder<TMarker> b, ApplyHookContext c) => body(b, c);
  }

  [Test]
  public async Task Hook_SetProperty_OverridesSpecField_AndLastRegisteredWinsAsync() {
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-A", "Active");

    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<_jobModel>(new _collectiveHook<_jobModel>((b, _) => b.SetProperty(j => j.Status, "first")))
      .Register<_jobModel>(new _collectiveHook<_jobModel>((b, _) => b.SetProperty(j => j.Status, "second")));

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
      .Register<object>(new _collectiveHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, sentinel)),
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
      .Register<_jobModel>(new _collectiveHook<_jobModel>((b, _) => b.RemoveSetter(j => j.Status)));

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
      .Register<_jobModel>(new _collectiveHook<_jobModel>((b, _) => b.AndWhere(j => j.Status == "Active")));

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
      .Register<_jobModel>(new _collectiveHook<_jobModel>((b, _) => b.ReplaceWhere(j => j.Status == "Draft")));

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

    // Gated on _statusModel — a class _jobModel is NOT assignable to; registered UNKEYED so the default stays.
    var hooks = WhizbangApplyHooks.CreateCollectiveWithDefaults()
      .Register<_statusModel>(new _collectiveHook<_statusModel>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, sentinel)));

    await _applyWithHooksAsync(hooks);

    var (updatedAt, version) = await _updatedAtVersionAsync(job);
    await Assert.That(updatedAt.Year).IsNotEqualTo(1999)
      .Because("The _unrelatedMarker hook does not match _jobModel, so its sentinel stamp never applies.");
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
    // q.Of<_statusModel>().Any(...), which the Dapper compiler turns into a correlated EXISTS over the
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

    var siblings = new Dictionary<Type, string> { [typeof(_statusModel)] = STATUS_TABLE };

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _crossEntry(),
      new _crossPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
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
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_crossPerspective),
    MethodName: nameof(_crossPerspective.Archive),
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_crossPerspective)h).Archive((_archiveEvent)e, q));

  private sealed class _statusModel {
    public string Status { get; set; } = "";
  }

  // Cross-perspective handler: scopes the mutated job table by a status on the SIBLING status table.
  private sealed class _crossPerspective {
    private static readonly string[] _eligible = ["Draft"];

    public ICollectiveSpec<_jobModel> Archive(_archiveEvent e, ICollectiveQuery q) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => q.Of<_statusModel>().Any(st => st.Id == r.Id && _eligible.Contains(st.Data.Status)));

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
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

    var handler = new _jobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel),
      EventType: typeof(_archiveEvent),
      HandlerType: typeof(_jobPerspective),
      MethodName: nameof(_jobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      entry,
      handler,
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
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

    var handler = new _jobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel),
      EventType: typeof(_archiveEvent),
      HandlerType: typeof(_jobPerspective),
      MethodName: nameof(_jobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

    var before = DateTime.UtcNow;
    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      entry, handler, new _archiveEvent { Scope = new TenantCollectiveScope("t-stamp") },
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

    var handler = new _jobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel),
      EventType: typeof(_archiveEvent),
      HandlerType: typeof(_jobPerspective),
      MethodName: nameof(_jobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      entry,
      handler,
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
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

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Framework),
      new _draftPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
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

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Custom),
      new _draftPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
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
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_draftPerspective),
    MethodName: nameof(_draftPerspective.ArchiveDrafts),
    ScopeHandling: handling,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_draftPerspective)h).ArchiveDrafts((_archiveEvent)e));

  private sealed class _jobModel {
    public string Status { get; set; } = "";
  }

  // Projects the cohort onto its own data column via spec.Where — the per-perspective projection capability.
  private sealed class _draftPerspective {
    public ICollectiveSpec<_jobModel> ArchiveDrafts(_archiveEvent e) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => r.Data.Status == "Draft");

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
  }

  private sealed class _jobPerspective {
    public ICollectiveSpec<_jobModel> Archive(_archiveEvent e) =>
      new _spec(s => s.SetProperty(j => j.Status, "Archived"));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
      : ICollectiveSpec<_jobModel>;
  }

  private sealed record _archiveEvent : ICollectiveEvent {
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

    var affected = await DapperCollectiveEventApplier<_overlayModel>.ApplyAsync(
      _setActiveEntry(),
      new _overlayActivePerspective(),
      new _setActiveEvent { OverlayId = target, GlobalTemplateId = gid, Scope = new TenantCollectiveScope("t-A") },
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
    ModelType: typeof(_overlayModel),
    EventType: typeof(_setActiveEvent),
    HandlerType: typeof(_overlayActivePerspective),
    MethodName: nameof(_overlayActivePerspective.SetActive),
    ScopeHandling: CollectiveScopeHandling.Custom,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_overlayActivePerspective)h).SetActive((_setActiveEvent)e));

  private sealed class _overlayModel {
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public Guid GlobalTemplateId { get; set; }
  }

  private sealed class _overlayActivePerspective {
    public ICollectiveSpec<_overlayModel> SetActive(_setActiveEvent e) =>
      new _whereSpec(
        s => s.SetProperty(o => o.IsActive, o => o.Id == e.OverlayId),
        r => r.Data.GlobalTemplateId == e.GlobalTemplateId);

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_overlayModel>>> Setters,
        Expression<Func<PerspectiveRow<_overlayModel>, bool>>? Where) : ICollectiveSpec<_overlayModel>;
  }

  private sealed record _setActiveEvent : ICollectiveEvent {
    public required Guid OverlayId { get; init; }
    public required Guid GlobalTemplateId { get; init; }
    public required CollectiveScope Scope { get; init; }
  }
}
