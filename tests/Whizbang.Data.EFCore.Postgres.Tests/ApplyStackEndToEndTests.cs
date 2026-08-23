#pragma warning disable CA1707

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;
using Whizbang.Core.ValueObjects;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The apply-stack surface end to end: HTTP request → minimal-API endpoint → shared reporter →
/// the Postgres query → a real database seeded through raw event-store pointers → JSON back.
/// One test drives the whole chain the VS Code extension's local-API mode uses; nothing is faked
/// except the HTTP transport (TestServer).
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/ApplyStackEndpoints.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresApplyStackQuery.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ApplyStackEndToEndTests : EFCoreTestBase {

  private async Task _seedStreamAsync(Guid streamId, params string[] eventTypes) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    for (var version = 1; version <= eventTypes.Length; version++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText =
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'TestAggregate', @event_type, @version, NOW())";
      cmd.Parameters.AddWithValue("event_id", (Guid)TrackedGuid.NewMedo());
      cmd.Parameters.AddWithValue("stream_id", streamId);
      cmd.Parameters.AddWithValue("event_type", eventTypes[version - 1]);
      cmd.Parameters.AddWithValue("version", version);
      await cmd.ExecuteNonQueryAsync();
    }
  }

  private IHost _buildHost() {
    return new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddScoped(_ => CreateDbContext());
          // The same registration shape the Postgres driver's turnkey TryAdd performs.
          s.AddSingleton<IApplyStackQuery>(sp => new EFCorePostgresApplyStackQuery(
            sp.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext)));
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangApplyStacks());
        });
      })
      .Build();
  }

  [Test]
  [Timeout(120000)]
  public async Task HttpToPostgresAndBack_SignaturesFlowAndDrillInAsync(CancellationToken cancellationToken) {
    var collapsedA = Guid.NewGuid();
    var collapsedB = Guid.NewGuid();
    var plain = Guid.NewGuid();
    await _seedStreamAsync(collapsedA, "Created", "Updated", "Updated", "Updated", "Closed");
    await _seedStreamAsync(collapsedB, "Created", "Updated", "Updated", "Closed");
    await _seedStreamAsync(plain, "Created", "Updated", "Closed");

    using var host = _buildHost();
    await host.StartAsync(cancellationToken);
    var client = host.GetTestClient();

    // Signatures + anchored flow, one request — what the extension's flow visual issues.
    var body = await client.GetStringAsync("/whizbang/apply-stacks?anchor=Updated&radius=1", cancellationToken);
    using var json = JsonDocument.Parse(body);
    var root = json.RootElement;

    await Assert.That(root.GetProperty("available").GetBoolean()).IsTrue();
    var signatures = root.GetProperty("signatures");
    await Assert.That(signatures.GetArrayLength()).IsEqualTo(2)
      .Because("3× and 2× Updated collapse to the same shape; 1× is a different one — computed in SQL, served over HTTP");
    var heaviest = signatures[0];
    await Assert.That(heaviest.GetProperty("path")[1].GetString()).IsEqualTo("Updated+");
    await Assert.That(heaviest.GetProperty("streamCount").GetInt64()).IsEqualTo(2L);

    // The flow view merges BOTH shapes at the anchor column: 'Updated' anchors 'Updated+' too.
    var nodes = root.GetProperty("flow").GetProperty("nodes").EnumerateArray().ToList();
    var anchorColumn = nodes.Where(n => n.GetProperty("offset").GetInt32() == 0).ToList();
    await Assert.That(anchorColumn.Sum(n => n.GetProperty("streamCount").GetInt64())).IsEqualTo(3L)
      .Because("all three streams pass through the anchor column, split across the plain and collapsed labels");

    // Drill-in: exactly the two streams behind the collapsed signature.
    var streamsBody = await client.GetStringAsync(
      "/whizbang/apply-stacks/streams?step=Created&step=Updated%2B&step=Closed", cancellationToken);
    using var streamsJson = JsonDocument.Parse(streamsBody);
    var ids = streamsJson.RootElement.GetProperty("streams").EnumerateArray()
      .Select(e => e.GetGuid()).ToList();
    await Assert.That(ids).Count().IsEqualTo(2);
    await Assert.That(ids).Contains(collapsedA);
    await Assert.That(ids).Contains(collapsedB);
    await Assert.That(ids).DoesNotContain(plain)
      .Because("drill-in resolves the exact streams the signature counted, end to end");

    await host.StopAsync(cancellationToken);
  }
}
