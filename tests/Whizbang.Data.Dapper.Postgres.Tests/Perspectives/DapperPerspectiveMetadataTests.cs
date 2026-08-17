using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Locks that the Dapper perspective store PERSISTS the applied event's
/// <see cref="PerspectiveMetadata"/> rather than writing an empty object.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPerspectiveStore{TModel}"/> already declares a metadata-bearing
/// <c>UpsertAsync</c> overload, but it is a DEFAULT INTERFACE METHOD whose body discards the
/// argument and delegates to the metadata-less overload. A store that implements only the
/// narrower overloads therefore inherits a silently lossy path — no compiler error, no runtime
/// failure, just an empty metadata object on every row. This is the same defect shape as the
/// event-store decorators that served interface defaults for <c>GetCommitSequenceAsync</c> and
/// <c>HasStreamEventsBeforeAsync</c>.
/// </para>
/// <para>
/// The loss is not limited to timestamps: event type, event id, correlation, causation and
/// commit sequence all vanish, so anything reading them from a Dapper-backed perspective sees
/// an empty object. The event timestamp specifically is the source of business time, which row
/// retention anchors on.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[NotInParallel("DapperPerspectiveStoreTests")]
public class DapperPerspectiveMetadataTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_metadata_test";
  private JsonSerializerOptions _jsonOptions = null!;

  [Before(Test)]
  public async Task CreatePerspectiveTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var createSql = $"CREATE TABLE IF NOT EXISTS {TABLE_NAME} (" +
        "id UUID PRIMARY KEY, " +
        "data JSONB NOT NULL, " +
        "metadata JSONB NOT NULL DEFAULT '{}'::jsonb, " +
        "scope JSONB NOT NULL DEFAULT '{}'::jsonb, " +
        "created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
        "updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
        "version INT NOT NULL DEFAULT 1)";
    await using var cmd = new NpgsqlCommand(createSql, conn);
    await cmd.ExecuteNonQueryAsync();

    _jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        DapperPerspectiveTestJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
  }

  [Test]
  public async Task DapperUpsert_WithMetadata_PersistsEventTimestampAsync() {
    // Typed as the INTERFACE deliberately: the metadata overload is a default interface method,
    // so it is not even visible on the concrete type — the only way to reach it is through
    // IPerspectiveStore, which is exactly how production calls it and exactly why the loss is
    // invisible at every call site.
#pragma warning disable CA1859 // The interface type is the point: the overload under test is a default interface method.
    IPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel> store =
      new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
#pragma warning restore CA1859
    var id = Guid.CreateVersion7();
    // An event that happened well in the past — the value business time must reflect, and the
    // one a wall-clock stamp would silently replace with "now".
    var occurredAt = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    var metadata = new PerspectiveMetadata {
      EventType = "TestOccurred",
      EventId = Guid.CreateVersion7().ToString(),
      Timestamp = occurredAt,
      CorrelationId = "corr-42",
    };

    await store.UpsertAsync(id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "test" }, new PerspectiveScope(), false, metadata);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"SELECT metadata ->> 'EventType', metadata ->> 'CorrelationId' FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();

    await Assert.That(reader.IsDBNull(0) ? null : reader.GetString(0)).IsEqualTo("TestOccurred")
      .Because("the Dapper store must persist the applied event's metadata, not an empty object — "
        + "the metadata-bearing UpsertAsync overload is a default interface method that silently discards it");
    await Assert.That(reader.IsDBNull(1) ? null : reader.GetString(1)).IsEqualTo("corr-42")
      .Because("correlation is lost with the rest of the metadata, not just the timestamp");
  }

  /// <summary>
  /// Drift-lock for the defect SHAPE: any <see cref="IPerspectiveStore{TModel}"/> implementation
  /// that leaves the metadata-bearing overload to the interface default inherits a body that
  /// discards metadata. Fails for a future store that forgets to override it.
  /// </summary>
  [Test]
  public async Task EveryPerspectiveStore_OverridesTheMetadataOverload_NotTheLossyDefaultAsync() {
    var storeTypes = new[] {
      typeof(DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>),
    };

    var swallowed = new List<string>();
    foreach (var storeType in storeTypes) {
      var map = storeType.GetInterfaceMap(typeof(IPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>));
      for (var i = 0; i < map.InterfaceMethods.Length; i++) {
        var declared = map.InterfaceMethods[i];
        if (declared.Name != nameof(IPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>.UpsertAsync)) {
          continue;
        }
        if (!declared.GetParameters().Any(p => p.ParameterType == typeof(PerspectiveMetadata))) {
          continue;
        }
        if (map.TargetMethods[i].DeclaringType?.IsInterface == true) {
          swallowed.Add(storeType.Name);
        }
      }
    }

    await Assert.That(swallowed).IsEmpty()
      .Because($"[{string.Join(", ", swallowed)}] serve the interface's default metadata overload, "
        + "whose body drops the argument — implement it and persist the metadata");
  }
}
