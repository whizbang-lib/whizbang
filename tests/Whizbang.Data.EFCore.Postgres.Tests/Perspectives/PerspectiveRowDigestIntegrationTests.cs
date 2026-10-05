using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The row digest a rebuild of named streams records before and after it runs (#1135).
/// </summary>
/// <remarks>
/// Cursor movement proves a rebuild executed; the digest is what shows whether anything changed. The two tests
/// that matter most here are the pair that pin which of those two questions the digest answers: a row whose
/// bookkeeping changed but whose content did not must digest the same, and a row whose content changed must not.
/// </remarks>
/// <tests>src/Whizbang.Data.EFCore.Postgres/Perspectives/EFCorePostgresPerspectiveRowDigest.cs</tests>
[Category("Integration")]
[Category("Shard3")]
public class PerspectiveRowDigestIntegrationTests : EFCoreTestBase {

  private const string TABLE = "wh_per_digest_probe";
  private const string SERVICE = "digest-tests";

  private async Task _createProbeTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb,
        metadata jsonb,
        version integer
      );
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _upsertRowAsync(Guid id, string dataJson, string metadataJson, int version) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO {TABLE} (id, data, metadata, version)
      VALUES (@id, @data::jsonb, @metadata::jsonb, @version)
      ON CONFLICT (id) DO UPDATE SET data = excluded.data,
                                     metadata = excluded.metadata,
                                     version = excluded.version;
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("data", dataJson);
    cmd.Parameters.AddWithValue("metadata", metadataJson);
    cmd.Parameters.AddWithValue("version", version);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _registerAsync(string clrTypeName) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    // Upsert on the table's real unique key. clr_type_name alone is NOT unique — the same model type can be
    // registered by more than one service — so the pair is what identifies a row.
    cmd.CommandText = """
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@clr, @table, '{}'::jsonb, 'hash', @service)
      ON CONFLICT (clr_type_name, service_name) DO UPDATE SET table_name = excluded.table_name;
      """;
    cmd.Parameters.AddWithValue("clr", clrTypeName);
    cmd.Parameters.AddWithValue("table", TABLE);
    cmd.Parameters.AddWithValue("service", SERVICE);
    await cmd.ExecuteNonQueryAsync();
  }

  private EFCorePostgresPerspectiveRowDigest _digest(string perspectiveName, string modelType) {
    var ctx = new WorkCoordinationDbContext(DbContextOptions);
    return new EFCorePostgresPerspectiveRowDigest(
        ctx, new OneEntryRegistry(perspectiveName, modelType), new FixedServiceInstance(SERVICE));
  }

  [Test]
  public async Task NoTargetedStreams_YieldsNullRatherThanADigestOfNothingAsync() {
    var digest = _digest("P", "M");

    var result = await digest.ComputeAsync("P", [], CancellationToken.None);

    // A digest of the empty set would compare equal to another digest of the empty set, and so would read as
    // "the rebuild changed nothing". It cannot make that claim.
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task UnresolvablePerspective_YieldsNullAsync() {
    var digest = _digest("P", "M");

    var result = await digest.ComputeAsync("NotTheOneRegistered", [Guid.NewGuid()],
        CancellationToken.None);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task SameContent_DigestsTheSameAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");
    var id = Guid.NewGuid();
    await _upsertRowAsync(id, """{"a":1,"b":2}""", """{"EventId":"x"}""", 1);

    var digest = _digest("Probe.Projection", "Model.Probe");
    var first = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);
    var second = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await Assert.That(first).IsNotNull();
    await Assert.That(second).IsEqualTo(first);
  }

  // jsonb is stored normalized, so the same content written with different key order and whitespace is the same
  // text coming back out. This is what lets the digest avoid defining a canonical form of its own.
  [Test]
  public async Task SameContentWrittenDifferently_DigestsTheSameAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");
    var id = Guid.NewGuid();

    var digest = _digest("Probe.Projection", "Model.Probe");
    await _upsertRowAsync(id, """{"a":1,"b":2}""", """{"EventId":"x"}""", 1);
    var compact = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await _upsertRowAsync(id, """{ "b" : 2,   "a" : 1 }""", """{"EventId":"x"}""", 1);
    var reordered = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await Assert.That(reordered).IsEqualTo(compact);
  }

  // The distinction the digest exists to draw: bookkeeping moved, content did not.
  [Test]
  public async Task MetadataOnlyChange_DigestsTheSameAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");
    var id = Guid.NewGuid();

    var digest = _digest("Probe.Projection", "Model.Probe");
    await _upsertRowAsync(id, """{"a":1}""", """{"EventId":"first","CommitSequence":1}""", 1);
    var before = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await _upsertRowAsync(id, """{"a":1}""", """{"EventId":"second","CommitSequence":99}""", 1);
    var after = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await Assert.That(after).IsEqualTo(before);
  }

  [Test]
  public async Task ContentChange_DigestsDifferentlyAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");
    var id = Guid.NewGuid();

    var digest = _digest("Probe.Projection", "Model.Probe");
    await _upsertRowAsync(id, """{"a":1}""", """{"EventId":"x"}""", 1);
    var before = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await _upsertRowAsync(id, """{"a":2}""", """{"EventId":"x"}""", 1);
    var after = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await Assert.That(after).IsNotEqualTo(before);
  }

  [Test]
  public async Task VersionChange_DigestsDifferentlyAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");
    var id = Guid.NewGuid();

    var digest = _digest("Probe.Projection", "Model.Probe");
    await _upsertRowAsync(id, """{"a":1}""", """{"EventId":"x"}""", 1);
    var before = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await _upsertRowAsync(id, """{"a":1}""", """{"EventId":"x"}""", 2);
    var after = await digest.ComputeAsync("Probe.Projection", [id], CancellationToken.None);

    await Assert.That(after).IsNotEqualTo(before);
  }

  [Test]
  public async Task TargetedRowThatDoesNotExistYet_ReportsEmptyNotNullAsync() {
    await _createProbeTableAsync();
    await _registerAsync("Model.Probe");

    var digest = _digest("Probe.Projection", "Model.Probe");
    var result = await digest.ComputeAsync("Probe.Projection", [Guid.NewGuid()],
        CancellationToken.None);

    // "no row yet" is a real state and distinct from "could not compute", so it gets a marker of its own.
    await Assert.That(result).IsEqualTo("empty");
  }

  /// <summary>Reports a fixed service name, so the registry lookup's service scoping is under test control.</summary>
  private sealed class FixedServiceInstance(string serviceName) : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;

    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>Registry stub reporting exactly one perspective, so the name-to-model hop is under test control.</summary>
  private sealed class OneEntryRegistry(string clrTypeName, string modelType) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(clrTypeName, clrTypeName, modelType, [])];

    public IReadOnlySet<Whizbang.Core.Messaging.LifecycleStage> LifecycleStagesWithReceptors =>
      new HashSet<Whizbang.Core.Messaging.LifecycleStage>();

    public IReadOnlyList<Type> GetEventTypes() => [];
  }
}
