// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Capabilities;

/// <summary>
/// pgvector is an explicit requirement of every vector column: the column cannot exist without it. The library
/// creates it where the server allows, before the data source is built, so the startup verdict, which probes
/// through that data source, finds it Ready; where the server refuses, start fails with the verdict line naming
/// the feature, the column and the fix, instead of a raw exception.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresRequiredExtension.cs</code-under-test>
/// <docs>fundamentals/perspectives/vector-fields#pgvector-extension</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class RequiredVectorExtensionTests : IAsyncDisposable {
  private const string SCOPE = "db";
  private string? _databaseName;
  private string _connectionString = null!;
  private string? _role;

  private static readonly IReadOnlyList<ActiveCapabilityRequirement> _requirements =
    PostgresSchemaRequirements.From(["wh_per_product.embedding"]);

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("reqvec");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await _executeAsync(_connectionString, "DROP EXTENSION IF EXISTS vector");
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
      _databaseName = null;
    }
    if (_role is not null) {
      await _executeAsync(SharedPostgresContainer.ConnectionString, $"DROP ROLE IF EXISTS {_role}");
      _role = null;
    }
    GC.SuppressFinalize(this);
  }

  private static async Task _executeAsync(string connectionString, string sql) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>A role that may connect and create in the database but is not a superuser.</summary>
  private async Task<string> _limitedRoleAsync() {
    _role = $"wh_limited_{Guid.NewGuid():N}";
    await _executeAsync(SharedPostgresContainer.ConnectionString, $"CREATE ROLE {_role} LOGIN PASSWORD 'limited'");
    await _executeAsync(SharedPostgresContainer.ConnectionString, $"GRANT CONNECT, CREATE ON DATABASE {_databaseName} TO {_role}");
    return new NpgsqlConnectionStringBuilder(_connectionString) { Username = _role, Password = "limited", Pooling = false }.ConnectionString;
  }

  [Test]
  public async Task AvailableButNotInstalled_StartSucceeds_BecauseTheProbeFindsWhatTheDataSourceCreatedAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    // The shape the generator emits: pgvector is ensured before Build(), inside the data source factory.
    services.AddSingleton(_ => {
      var builder = new NpgsqlDataSourceBuilder(_connectionString);
      builder.UseVector();
      using (var tempConn = new NpgsqlConnection(_connectionString)) {
        PostgresRequiredExtension.EnsureAsync(tempConn, "vector", SCOPE, _requirements, CancellationToken.None).GetAwaiter().GetResult();
      }
      return builder.Build();
    });
    services.AddSingleton<ICapabilityRequirementSource>(new FixedCapabilityRequirementSource(_requirements));
    services.AddWhizbangPostgresCapabilities(SCOPE, (sp, ct) => sp.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct));
    await using var provider = services.BuildServiceProvider();
    var installedBefore = await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'");

    await provider.GetServices<IStartupStep>().OfType<CapabilitiesStartupStep>().Single().ExecuteAsync(CancellationToken.None);

    var verdict = provider.GetRequiredService<ICapabilityRegistry>().Verdict;
    await Assert.That(installedBefore).IsEqualTo("0");
    await Assert.That(verdict.Failures).IsEmpty()
      .Because("a server that only offers pgvector is one the library creates it on before anything probes");
    var entry = verdict.Entries.Single();
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
    await Assert.That(entry.Line).StartsWith($"capability postgres/vector on '{SCOPE}': Ready, ");
    await Assert.That(entry.Line).EndsWith("Required by: Vector fields on perspectives (wh_per_product.embedding, explicit)");
  }

  [Test]
  public async Task RefusedByTheServer_StartFails_NamingTheFeatureTheColumnAndTheFixAsync() {
    var limited = await _limitedRoleAsync();
    await using var connection = new NpgsqlConnection(limited);
    var otherProvider = new ActiveCapabilityRequirement(
      new CapabilityRequirement(new CapabilityId("broker", "vector"), "A broker feature", "W:Broker", null, null), "W:Broker", null, CapabilityActivation.Explicit);
    var otherExtension = new ActiveCapabilityRequirement(
      new CapabilityRequirement(new CapabilityId("postgres", "pg_trgm"), "Substring indexes", "wh_per_product", null, null), "wh_per_product", null, CapabilityActivation.Implicit);

    var failure = await Assert.That(async () =>
      await PostgresRequiredExtension.EnsureAsync(connection, "vector", SCOPE, [otherProvider, .. _requirements, otherExtension], CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).IsEqualTo(
      $"capability postgres/vector on '{SCOPE}': NoPrivilege; NoPrivilege. Fix: run CREATE EXTENSION vector as a role allowed to "
      + "create it (a superuser or the server's administrator role), or grant this role CREATE on the database. "
      + "Required by: Vector fields on perspectives (wh_per_product.embedding, explicit)");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'")).IsEqualTo("0");
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("a connection the helper opened is closed again, even when it throws");
  }

  [Test]
  public async Task RefusedOnlyWhenCreated_StartFails_WithTheServersAnswerAsync() {
    // An extension that needs preloading reads Unknown to a role that cannot read shared_preload_libraries, so
    // the helper tries to create it; the server refuses then, and the fallback names what it said.
    await _executeAsync(_connectionString, "DROP EXTENSION IF EXISTS pg_stat_statements");
    var limited = await _limitedRoleAsync();
    await using var connection = new NpgsqlConnection(limited);
    await connection.OpenAsync();
    var statistics = new ActiveCapabilityRequirement(
      new CapabilityRequirement(new CapabilityId("postgres", "pg_stat_statements"), "Expensive-predicate advice", "W:Advisor:Enabled", "true", "false"),
      "W:Advisor:Enabled", "true", CapabilityActivation.Explicit);

    var failure = await Assert.That(async () =>
      await PostgresRequiredExtension.EnsureAsync(connection, "pg_stat_statements", SCOPE, [statistics], CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).StartsWith(
      $"capability postgres/pg_stat_statements on '{SCOPE}': NoPrivilege; 42501: permission denied to create extension \"pg_stat_statements\". Fix: ");
    await Assert.That(failure.Message).EndsWith("Required by: Expensive-predicate advice (W:Advisor:Enabled = true, explicit)");
    await Assert.That(failure.InnerException).IsTypeOf<PostgresException>();
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open)
      .Because("a connection the caller opened is the caller's to close");
  }

  [Test]
  public async Task AlreadyInstalled_IsLeftAlone_EvenForARoleThatCouldNotCreateItAsync() {
    await _executeAsync(_connectionString, "CREATE EXTENSION vector");
    var limited = await _limitedRoleAsync();
    await using var connection = new NpgsqlConnection(limited);

    await PostgresRequiredExtension.EnsureAsync(connection, "vector", SCOPE, _requirements, CancellationToken.None);

    await Assert.That(await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'")).IsEqualTo("1");
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await Assert.That(async () => await PostgresRequiredExtension.EnsureAsync(null!, "vector", SCOPE, _requirements, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await PostgresRequiredExtension.EnsureAsync(connection, null!, SCOPE, _requirements, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await PostgresRequiredExtension.EnsureAsync(connection, "vector", null!, _requirements, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await PostgresRequiredExtension.EnsureAsync(connection, "vector", SCOPE, null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }
}
