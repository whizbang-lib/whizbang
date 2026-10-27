// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Capabilities;

/// <summary>
/// A host that probes two databases, one carrying a context with vector columns and one without: the context's
/// requirement on pgvector is judged on its own database only. Judged on every database, a store on the second
/// database, which has no vector column and no pgvector, would fail start for nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCapabilityProvider.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityVerdict.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class TwoDatabaseHostRequirementTests : IAsyncDisposable {
  private const string VECTORS = "vectors";
  private const string PLAIN = "plain";

  /// <summary>Stands for the context whose generated schema requirements are keyed under its type.</summary>
  private sealed class VectorContext;

  /// <summary>A context the generator recorded nothing for.</summary>
  private sealed class PlainContext;

  private string? _vectorDatabase;
  private string? _plainDatabase;
  private string _vectorConnection = null!;
  private string _plainConnection = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var vectors = await PerTestDatabaseFactory.CreateAsync("twodbvec");
    var plain = await PerTestDatabaseFactory.CreateAsync("twodbplain");
    (_vectorDatabase, _vectorConnection) = (vectors.Name, vectors.ConnectionString);
    (_plainDatabase, _plainConnection) = (plain.Name, plain.ConnectionString);
    await _executeAsync(_vectorConnection, "DROP EXTENSION IF EXISTS vector");
    await _executeAsync(_plainConnection, "DROP EXTENSION IF EXISTS vector");
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    foreach (var name in new[] { _vectorDatabase, _plainDatabase }.OfType<string>()) {
      await PerTestDatabaseFactory.DropAsync(name);
    }
    (_vectorDatabase, _plainDatabase) = (null, null);
    GC.SuppressFinalize(this);
  }

  private static async Task _executeAsync(string connectionString, string sql) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<CapabilityVerdict> _verdictAsync(params (string Key, string Value)[] settings) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
      .Build());
    services.AddWhizbangPostgresCapabilities(VECTORS, async (_, ct) => {
      var connection = new NpgsqlConnection(_vectorConnection);
      await connection.OpenAsync(ct);
      return connection;
    });
    services.AddWhizbangPostgresCapabilities(PLAIN, async (_, ct) => {
      var connection = new NpgsqlConnection(_plainConnection);
      await connection.OpenAsync(ct);
      return connection;
    });
    // What the generated registration declares for the vector context, keyed by the context.
    services.AddKeyedSingleton<ICapabilityRequirementSource>(typeof(VectorContext),
      new FixedCapabilityRequirementSource(PostgresSchemaRequirements.From(["wh_per_product.embedding"], [])));
    // What the EF Core driver registers for each context it is given, under that context's database.
    services.AddWhizbangPostgresSchemaRequirements(typeof(VectorContext), VECTORS);
    services.AddWhizbangPostgresSchemaRequirements(typeof(PlainContext), PLAIN);
    await using var provider = services.BuildServiceProvider();

    await provider.GetServices<IStartupStep>().OfType<CapabilitiesStartupStep>().Single().ExecuteAsync(CancellationToken.None);
    return provider.GetRequiredService<ICapabilityRegistry>().Verdict;
  }

  [Test]
  public async Task VectorOnlyOnTheVectorDatabase_StartSucceedsAsync() {
    await _executeAsync(_vectorConnection, "CREATE EXTENSION vector");

    var verdict = await _verdictAsync();

    await Assert.That(verdict.Failures).IsEmpty()
      .Because("the database without vector columns is not asked for pgvector");
    var entry = verdict.Entries.Single();
    await Assert.That(entry.Status.Scope).IsEqualTo(VECTORS);
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
  }

  [Test]
  public async Task TheVectorDatabaseWithoutTheExtension_IsProvisionedThere_AndNowhereElseAsync() {
    var verdict = await _verdictAsync();

    var entry = verdict.Entries.Single();
    await Assert.That(entry.Status.Scope).IsEqualTo(VECTORS);
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed)
      .Because("provisioning creates a required extension the server offers before the verdict judges it");
    await Assert.That(entry.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Created);
    await Assert.That(await _hasVectorAsync(_vectorConnection)).IsTrue();
    await Assert.That(await _hasVectorAsync(_plainConnection)).IsFalse()
      .Because("a requirement scoped to one database is provisioned on that database only");
  }

  private static async Task<bool> _hasVectorAsync(string connectionString) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')", connection);
    return await command.ExecuteScalarAsync() is true;
  }

  [Test]
  public async Task TheVectorDatabaseWithoutTheExtension_AndProvisioningOff_FailsStartNamingItAsync() {
    var verdict = await _verdictAsync(("Whizbang:Capabilities:postgres:vector:AutoProvision", "false"));

    var failure = verdict.Failures.Single();
    await Assert.That(failure.Status.Scope).IsEqualTo(VECTORS);
    await Assert.That(failure.Line).StartsWith($"capability postgres/vector on '{VECTORS}': Provisionable");
    await Assert.That(failure.Line).EndsWith("Required by: Vector fields on perspectives (wh_per_product.embedding, explicit)");
  }
}
