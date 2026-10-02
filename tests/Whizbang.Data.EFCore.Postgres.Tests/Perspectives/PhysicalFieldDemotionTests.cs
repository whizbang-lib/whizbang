using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1022: a field that is no longer physical has the values its column holds copied into the document for
/// every row, and again for every row either release writes during the deploy; the column is left in place.
/// Moves out of a secondary jsonb column, and out of an array or an instant column, are covered alike.
/// </summary>
/// <remarks>
/// Each test first puts the table in the shape the previous release left it, which kept <c>Score</c>,
/// <c>Notes</c> (jsonb), <c>Labels</c> (text[]) and <c>SeenAt</c> in columns on a Split model: the values in the
/// columns and the document holding the defaults the Split runner strips them to. The perspective's schema hash is
/// then forgotten so the next start applies the release that no longer promotes them.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#demoting-a-field</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalFieldDemotionTests {
  private const string TABLE = PhysicalMoves.TABLE;
  private static readonly Guid _row = Guid.Parse("5c0f1d2e-0000-4000-8000-0000000000bb");

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("phys_demote");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await PhysicalMoves.StartAsync(_connectionString);
    await _demoteAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private Task<string> _scalarAsync(string sql) => PhysicalMoves.ScalarAsync(_connectionString, sql);

  private Task _execAsync(string sql) => PhysicalMoves.ExecAsync(_connectionString, sql);

  private const string STRIPPED = """{"Score": 0, "Notes": null, "Labels": null, "SeenAt": null}""";

  /// <summary>The table as the release that promoted the fields left it, 1,500 rows and one known row; then the demoting start.</summary>
  private async Task _demoteAsync() {
    await _execAsync($$"""
      ALTER TABLE {{TABLE}} ADD COLUMN score integer, ADD COLUMN notes jsonb, ADD COLUMN labels text[], ADD COLUMN seen_at timestamptz;
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version,
                           score, notes, labels, seen_at)
      SELECT gen_random_uuid(), '{{STRIPPED}}'::jsonb, '{}', '{}', now(), now(), now(), now(), 1,
             g, jsonb_build_array(jsonb_build_object('Label', 'n' || g, 'Weight', g)), ARRAY['x' || g, 'y'],
             TIMESTAMPTZ 'epoch' + g * INTERVAL '1 microsecond'
      FROM generate_series(1, 1500) AS g;
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version,
                           score, notes, labels, seen_at)
      VALUES ('{{_row}}', '{{STRIPPED}}'::jsonb, '{}', '{}', now(), now(), now(), now(), 1,
              42, '[{"Label": "k", "Weight": 3}]', ARRAY['a', 'b'], TIMESTAMPTZ '2026-03-04 05:06:07.123456+00');
      """);
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);
    await PhysicalMoves.StartAsync(_connectionString);
  }

  private Task<string> _documentAsync() => _scalarAsync(
    $"SELECT concat_ws('|', data ->> 'Score', data -> 'Notes', data -> 'Labels', data ->> 'SeenAt') FROM {TABLE} WHERE id = '{_row}'");

  private Task<string> _columnsAsync() => _scalarAsync(
    $"SELECT concat_ws('|', score, notes, labels::text, (EXTRACT(EPOCH FROM seen_at) * 1000000)::bigint) FROM {TABLE} WHERE id = '{_row}'");

  private async Task _settleAsync() {
    await using var context = PhysicalMoves.Context(_connectionString);
    var services = new ServiceCollection();
    services.AddSingleton(context);
    services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    await using var provider = services.BuildServiceProvider();
    var step = new PhysicalColumnFillMaintenanceStep(
      typeof(PhysicalMovesDbContext), null, new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 3, 0, TimeSpan.Zero)), settle: TimeSpan.Zero);
    await step.RunAsync(provider, CancellationToken.None);
  }

  /// <summary>Every row's document holds what its column held, in the form the document stores; the columns stay.</summary>
  [Test]
  public async Task ADemotedField_IsCopiedIntoTheDocumentForEveryRowAsync() {
    await Assert.That(await _documentAsync())
      .IsEqualTo("42|[{\"Label\": \"k\", \"Weight\": 3}]|[\"a\", \"b\"]|1772600767123456");
    await Assert.That(await _scalarAsync($"""
      SELECT count(*) FROM {TABLE}
      WHERE (data ->> 'Score')::int IS DISTINCT FROM score OR data -> 'Notes' IS DISTINCT FROM notes
         OR data -> 'Labels' IS DISTINCT FROM to_jsonb(labels)
         OR (data ->> 'SeenAt')::bigint IS DISTINCT FROM (EXTRACT(EPOCH FROM seen_at) * 1000000)::bigint
      """)).IsEqualTo("0");
    await Assert.That(await _columnsAsync()).IsEqualTo("42|[{\"Label\": \"k\", \"Weight\": 3}]|{a,b}|1772600767123456")
      .Because("a demotion never drops or clears the column; an operator drops it");
    await Assert.That(await _scalarAsync("""
      SELECT string_agg(column_name || ':' || direction || ':' || sync_writes, ',' ORDER BY column_name)
      FROM wh_physical_column_fills
      """)).IsEqualTo("labels:to_document:true,notes:to_document:true,score:to_document:true,seen_at:to_document:true");
  }

  /// <summary>
  /// The previous release writes the columns and strips the document; the document follows. The new release
  /// writes only the document; the column follows, so the previous release still reads it right.
  /// </summary>
  [Test]
  public async Task AWriteFromEitherRelease_KeepsTheColumnAndTheDocumentInAgreementAsync() {
    await _execAsync($"""
      UPDATE {TABLE} SET data = data || '{STRIPPED}'::jsonb, score = 50, notes = NULL, labels = ARRAY['c'], seen_at = NULL
      WHERE id = '{_row}'
      """);
    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Score', data -> 'Labels') FROM {TABLE} WHERE id = '{_row}'"))
      .IsEqualTo("50|[\"c\"]");
    await Assert.That(await _documentAsync()).IsEqualTo("50|null|[\"c\"]")
      .Because("the previous release wrote every column, two of them null, and the document follows each");

    await _execAsync($$"""UPDATE {{TABLE}} SET data = data || '{"Score": 51, "Notes": [], "SeenAt": 1}'::jsonb WHERE id = '{{_row}}'""");
    await Assert.That(await _columnsAsync()).IsEqualTo("51|[]|{c}|1");

    var late = Guid.NewGuid();
    await _execAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version, score)
      VALUES ('{{late}}', '{{STRIPPED}}'::jsonb, '{}', '{}', now(), now(), now(), now(), 1, 77)
      """);
    await Assert.That(await _scalarAsync($"SELECT data ->> 'Score' FROM {TABLE} WHERE id = '{late}'")).IsEqualTo("77");

    var fresh = Guid.NewGuid();
    await _execAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      VALUES ('{{fresh}}', '{"Score": 5, "Labels": ["z"]}', '{}', '{}', now(), now(), now(), now(), 1)
      """);
    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', score, labels::text) FROM {TABLE} WHERE id = '{fresh}'")).IsEqualTo("5|{z}");
  }

  /// <summary>A second start copies nothing: the demotion is recorded, so a column is moved once.</summary>
  [Test]
  public async Task ASecondPass_CopiesNothingAsync() {
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);
    await PhysicalMoves.StartAsync(_connectionString);

    await Assert.That(await _scalarAsync($$"""
      SELECT string_agg(outcome || ':' || copied, ',')
      FROM wh_demote_physical_columns('"public".{{TABLE}}', '{"score": "Score", "labels": "Labels"}'::jsonb)
      """)).IsEqualTo("moved earlier:0,moved earlier:0");
    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("8");
  }

  /// <summary>
  /// Once settled, the triggers are gone and the demotion is kept, so a later start never copies the column,
  /// by then stale, over the document the new release has been writing.
  /// </summary>
  [Test]
  public async Task ASettledDemotion_DropsItsTriggersAndIsNeverCopiedAgainAsync() {
    await _settleAsync();

    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("0");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction = 'to_document' AND settled_at IS NOT NULL"))
      .IsEqualTo("4");

    await _execAsync($"UPDATE {TABLE} SET data = jsonb_set(data, '{{Score}}', '9') WHERE id = '{_row}'");
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);
    await PhysicalMoves.StartAsync(_connectionString);
    await _settleAsync();

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Score', score) FROM {TABLE} WHERE id = '{_row}'"))
      .IsEqualTo("9|42").Because("the stale column is left for an operator to drop, and never read again");
  }

  /// <summary>A field promoted again after its demotion settled has its column refreshed from the document.</summary>
  [Test]
  public async Task PromotingADemotedFieldAgain_RefreshesTheColumnFromTheDocumentAsync() {
    await _settleAsync();
    await _execAsync($"UPDATE {TABLE} SET data = jsonb_set(data, '{{Score}}', '9') WHERE id = '{_row}'");

    var outcome = await _scalarAsync(
      $"SELECT wh_arm_physical_column('\"public\".{TABLE}', 'score', 'Score', '(data ->> ''Score'')::integer', true)");

    await Assert.That(outcome).IsEqualTo("refreshed");
    await Assert.That(await _scalarAsync($"SELECT score FROM {TABLE} WHERE id = '{_row}'")).IsEqualTo("9");
    await Assert.That(await _scalarAsync("SELECT direction || ':' || sync_writes FROM wh_physical_column_fills WHERE column_name = 'score'"))
      .IsEqualTo("to_column:true");
    await Assert.That(await _scalarAsync("SELECT wh_sync_physical_moves('\"public\".wh_per_moved_ticket')")).IsEqualTo("1")
      .Because("the promotion is a Split one again, so its writes are synced for the deploy");
  }

  /// <summary>A demoted field promoted again whose column the document cannot fill is a rebuild notice.</summary>
  [Test]
  public async Task PromotingADemotedFieldAgain_WithNoExtraction_IsARebuildNoticeAsync() {
    var outcome = await _scalarAsync($"SELECT wh_arm_physical_column('\"public\".{TABLE}', 'score', 'Score', NULL, false)");

    await Assert.That(outcome).IsEqualTo("rebuild");
    await Assert.That(await _scalarAsync("SELECT direction FROM wh_physical_column_fills WHERE column_name = 'score'")).IsEqualTo("rebuild");
  }

  /// <summary>A field demoted while its promotion is still armed is moved back, and its promotion triggers go.</summary>
  [Test]
  public async Task DemotingAFieldWhosePromotionIsStillArmed_MovesItBackAsync() {
    await _execAsync("UPDATE wh_physical_column_fills SET direction = 'to_column' WHERE column_name = 'score'");

    var outcome = await _scalarAsync(
      $"SELECT outcome FROM wh_demote_physical_columns('\"public\".{TABLE}', '{{\"score\": \"Score\"}}'::jsonb)");

    await Assert.That(outcome).IsEqualTo("moved");
    await Assert.That(await _scalarAsync("SELECT direction FROM wh_physical_column_fills WHERE column_name = 'score'")).IsEqualTo("to_document");
    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("8");
  }
}
