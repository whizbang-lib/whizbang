#pragma warning disable CA1707

using System.Linq.Expressions;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Ordering comparisons in a collective <c>Where</c>, against a real Postgres through the Dapper applier. Each scenario
/// seeds the same three rows, applies the collective live, and evaluates the same spec with the in-memory replay
/// against the models those rows hold: the two must select the same rows. The rows are chosen to catch the ways the
/// two can part: a number that orders differently as text (<c>10</c> against <c>9</c>), a JSON null, a missing key, a
/// temporal stored as its microsecond count, an enumeration, and a physical column.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectivePredicateSqlCompiler.cs</tests>
[NotInParallel("PostgreSQL")]
[Category("CollectiveEvents")]
public class DapperCollectiveOrderingIntegrationTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_ranked";
  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

  private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-00000000000a");
  private static readonly Guid _b = Guid.Parse("00000000-0000-7000-8000-00000000000b");
  private static readonly Guid _c = Guid.Parse("00000000-0000-7000-8000-00000000000c");

  static DapperCollectiveOrderingIntegrationTests() {
    PerspectivePhysicalFieldRegistry.Register(typeof(RankedModel), nameof(RankedModel.Rank), "rank", FieldStorageMode.Split);
  }

  [Test]
  public async Task Ordering_OnANumberThatOrdersDifferentlyAsText_ComparesNumericallyAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Ordinal < 9, _a);

  [Test]
  public async Task Ordering_GreaterThanOrEqual_SelectsTheBoundaryAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Ordinal >= 9, _b, _c);

  [Test]
  public async Task Ordering_OnANullableMember_ExcludesTheNullAndTheMissingKeyAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.MaybeOrdinal < 5, _a);

  [Test]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1940:Boolean checks should not be inverted", Justification = "The negated ordering is the case under test.")]
  public async Task Ordering_NegatedOverANullableMember_SelectsTheNullAndTheMissingKeyAsync() =>
#pragma warning disable RCS1068 // the negation of an ordering is the shape under test
    await _assertLiveMatchesReplayAsync(r => !(r.Data.MaybeOrdinal < 5), _b, _c);
#pragma warning restore RCS1068

  [Test]
  public async Task Ordering_Coalesced_CountsTheMissingKeyAsTheDefaultAsync() =>
    await _assertLiveMatchesReplayAsync(r => (r.Data.MaybeOrdinal ?? 0) < 5, _a, _b, _c);

  [Test]
  public async Task Ordering_OnAStoredInstant_ComparesTheMicrosecondCountAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.ActivatedAt > _t0, _b, _c);

  [Test]
  public async Task Ordering_OnANullableInstant_ExcludesTheMissingKeyAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.RetiredAt <= _t0, _a);

  [Test]
  public async Task Ordering_OnAnEnumeration_ComparesTheUnderlyingNumberAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Stage > RankStage.Draft, _a, _b);

  [Test]
  public async Task Ordering_OnAPhysicalColumn_ComparesTheColumnAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Rank > 2, _b, _c);

  [Test]
  public async Task Ordering_ChainedWithAnotherOrdering_SelectsTheIntersectionAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Rank <= 3 && r.Data.Ordinal > 3, _c);

  [Test]
  public async Task Ordering_ARenderedTemporal_IsRefusedRatherThanAnsweredAsync() {
    await _createTableAsync();
    using (var conn = await ConnectionFactory.CreateConnectionAsync()) {
      await conn.ExecuteAsync(
        $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, @data::jsonb, '{{\"t\": \"t-A\"}}'::jsonb)",
        new { id = _a, data = "{\"ActivatedAt\": \"2026-03-01T12:00:00+00:00\", \"Title\": \"a\"}" });
    }

    await Assert.That(async () => await _applyAsync(new RankedSpec(s => s.SetProperty(m => m.Title, "hit"), r => r.Data.ActivatedAt > _t0)))
      .Throws<Npgsql.PostgresException>()
      .Because("A key still holding a rendering cannot be ordered as a number. Refusing is the documented behavior; "
        + "silently excluding the row would answer the query wrongly.");
  }

  // ── Scenario runner ─────────────────────────────────────────────────────────────────────────

  // Seeds the three rows, applies the collective live, and replays the same spec against the models the rows hold.
  private async Task _assertLiveMatchesReplayAsync(Expression<Func<PerspectiveRow<RankedModel>, bool>> where, params Guid[] expected) {
    await _createTableAsync();
    var models = await _seedAsync();
    var spec = new RankedSpec(s => s.SetProperty(m => m.Title, "hit"), where);

    await _applyAsync(spec);

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var live = (await conn.QueryAsync<Guid>($"SELECT id FROM {TABLE} WHERE data->>'Title' = 'hit' ORDER BY id")).ToList();
    var replayed = models
      .Where(m => CollectiveInMemoryEvaluator<RankedModel>.Matches(spec, m.Key, m.Value))
      .Select(m => m.Key)
      .Order()
      .ToList();

    await Assert.That(live).IsEquivalentTo(expected);
    await Assert.That(replayed).IsEquivalentTo(live)
      .Because("The in-memory replay must select exactly the rows the live apply selected.");
  }

  // a: every key present. b: a larger number, a JSON null and a later instant. c: the nullable keys missing, the
  // way a row written before the property existed stores it.
  private async Task<Dictionary<Guid, RankedModel>> _seedAsync() {
    var t1 = _t0.AddDays(1);
    var t2 = _t0.AddDays(2);
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await _insertAsync(conn, _a, 1,
      $"{{\"Ordinal\": 3, \"MaybeOrdinal\": 3, \"ActivatedAt\": {_us(_t0)}, \"RetiredAt\": {_us(_t0)}, \"Stage\": 1, \"Title\": \"a\"}}");
    await _insertAsync(conn, _b, 5,
      $"{{\"Ordinal\": 10, \"MaybeOrdinal\": null, \"ActivatedAt\": {_us(t1)}, \"RetiredAt\": null, \"Stage\": 2, \"Title\": \"b\"}}");
    await _insertAsync(conn, _c, 3,
      $"{{\"Ordinal\": 9, \"ActivatedAt\": {_us(t2)}, \"Stage\": 0, \"Title\": \"c\"}}");
    return new Dictionary<Guid, RankedModel> {
      [_a] = new() { Ordinal = 3, MaybeOrdinal = 3, ActivatedAt = _t0, RetiredAt = _t0, Stage = RankStage.Active, Rank = 1, Title = "a" },
      [_b] = new() { Ordinal = 10, ActivatedAt = t1, Stage = RankStage.Retired, Rank = 5, Title = "b" },
      [_c] = new() { Ordinal = 9, ActivatedAt = t2, Stage = RankStage.Draft, Rank = 3, Title = "c" },
    };
  }

  private static string _us(DateTimeOffset value) =>
    CanonicalTemporalFormat.ToEpochMicroseconds(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static Task<int> _insertAsync(System.Data.IDbConnection conn, Guid id, int rank, string data) =>
    conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope, rank) VALUES (@id, @data::jsonb, '{{\"t\": \"t-A\"}}'::jsonb, @rank)",
      new { id, data, rank });

  private async Task _createTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, rank integer NOT NULL DEFAULT 0)
      """);
  }

  private Task<int> _applyAsync(RankedSpec spec) {
    var entry = new CollectiveApplyEntry(
      typeof(RankedModel), typeof(RankedCollectiveEvent), typeof(SpecHandler), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, (_, _, _) => spec);
    return DapperCollectiveEventApplier<RankedModel>.ApplyAsync(
      entry, new SpecHandler(), new RankedCollectiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default);
  }

  // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

  [SuppressIndexAdvisory("test fixture; the table holds three rows")]
  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class RankedModel {
    public long Ordinal { get; set; }
    public long? MaybeOrdinal { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public RankStage Stage { get; set; }
    [PhysicalField] public int Rank { get; set; }
    public string Title { get; set; } = "";
  }

  internal enum RankStage { Draft, Active, Retired }

  private sealed record RankedSpec(
      Expression<Action<ICollectiveSetters<RankedModel>>> Setters,
      Expression<Func<PerspectiveRow<RankedModel>, bool>>? Where = null) : ICollectiveSpec<RankedModel>;

  internal sealed record RankedCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class SpecHandler;
}
