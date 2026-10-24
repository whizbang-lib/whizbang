// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Postgres provisioner against a real server: <c>CREATE EXTENSION</c> and <c>ALTER EXTENSION ... UPDATE</c> run
/// in one transaction holding the schema lock, the result is read back and recorded as created, already present or
/// upgraded, and a refusal comes back as the state it reveals rather than as an exception. pg_trgm is the subject:
/// the test server ships it with update scripts from 1.3, so an upgrade is real.
/// </summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCapabilityProvisioner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityProvisioning.cs</code-under-test>
[Category("Integration")]
public class PostgresCapabilityProvisioningIntegrationTests {
  private const string SCOPE = "orders-db";
  private const string TRGM = "pg_trgm";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("ext_provision");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  /// <summary>A logger that completes a signal when the provisioner says it is waiting for the schema lock.</summary>
  private sealed class LockWaitSignal : ILogger<PostgresCapabilityProvisioner> {
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (eventId.Id == PostgresCapabilityProvisioner.WAITING_FOR_SCHEMA_LOCK_EVENT) {
        Waiting.TrySetResult();
      }
    }
  }

  private PostgresCapabilityDatabase _db(string? connectionString = null, string? afterOpen = null) =>
    new(SCOPE, async ct => {
      var connection = new NpgsqlConnection(connectionString ?? _database.ConnectionString);
      await connection.OpenAsync(ct);
      if (afterOpen is not null) {
        await using var command = connection.CreateCommand();
        command.CommandText = afterOpen;
        await command.ExecuteNonQueryAsync(ct);
      }
      return connection;
    });

  private PostgresCapabilityProvisioner _provisioner(
      string? connectionString = null, string? afterOpen = null, ILogger<PostgresCapabilityProvisioner>? logger = null) =>
    new([_db(connectionString, afterOpen)], logger ?? NullLogger<PostgresCapabilityProvisioner>.Instance);

  private async Task<CapabilityStatus> _probeAsync(string extension, string? connectionString = null, string? afterOpen = null) =>
    (await new PostgresCapabilityProvider([_db(connectionString, afterOpen)]).ProbeAsync(SCOPE, CancellationToken.None))
      .Single(s => s.Descriptor.Id.Name == extension);

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _createRoleAsync(string grants = "") {
    var role = $"ext_prov_{Guid.CreateVersion7():N}"[..40];
    await _executeAsync($"CREATE ROLE {role} LOGIN PASSWORD 'probe'; {grants.Replace("@role", role, StringComparison.Ordinal)}");
    return role;
  }

  private async Task _dropRoleAsync(string role) {
    NpgsqlConnection.ClearAllPools();
    await _executeAsync($"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};");
  }

  private string _as(string role) =>
    new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Username = role, Password = "probe" }.ConnectionString;

  [Test]
  public async Task TheProbe_ReadsTheVersionAnUpgradeWouldReachAsync() {
    var status = await _probeAsync(TRGM);

    await Assert.That(status.State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(status.AvailableVersion).IsNotNull()
      .Because("pg_available_extensions names the version CREATE EXTENSION installs and an update reaches");
  }

  [Test]
  public async Task Create_AnAvailableExtension_IsCreated_AndReadsCreatedAtItsVersionAsync() {
    var before = await _probeAsync(TRGM);

    var after = await _provisioner().ProvisionAsync(before, CapabilityProvisionAction.Create, CancellationToken.None);

    await Assert.That(after.State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Created);
    await Assert.That(after.Version).IsEqualTo(before.AvailableVersion);
    await Assert.That(after.Detail).IsEqualTo(nameof(PostgresExtensionState.Installed));
    await Assert.That(after.Scope).IsEqualTo(SCOPE);
    await Assert.That((await _probeAsync(TRGM)).State).IsEqualTo(CapabilityState.Ready)
      .Because("the transaction that created it committed");
  }

  [Test]
  public async Task Create_WhenSomethingElseCreatedItFirst_ReadsAlreadyPresentAsync() {
    var before = await _probeAsync(TRGM);
    await _executeAsync("CREATE EXTENSION pg_trgm");

    var after = await _provisioner().ProvisionAsync(before, CapabilityProvisionAction.Create, CancellationToken.None);

    await Assert.That(after.State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present)
      .Because("under the lock it reads the extension again, and another instance may have created it meanwhile");
  }

  [Test]
  public async Task Upgrade_AnOlderInstalledVersion_IsRecordedAsUpgradedAsync() {
    await _executeAsync("CREATE EXTENSION pg_trgm VERSION '1.3'");
    var before = await _probeAsync(TRGM);

    var after = await _provisioner().ProvisionAsync(before, CapabilityProvisionAction.Upgrade, CancellationToken.None);

    await Assert.That(before.Version).IsEqualTo("1.3");
    await Assert.That(CapabilityVersion.IsNewer(before.AvailableVersion, before.Version)).IsTrue();
    await Assert.That(after.State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Upgraded);
    await Assert.That(after.Version).IsEqualTo(before.AvailableVersion);
  }

  [Test]
  public async Task Upgrade_WhenSomethingElseUpgradedItFirst_ReadsAlreadyPresentAsync() {
    await _executeAsync("CREATE EXTENSION pg_trgm VERSION '1.3'");
    var before = await _probeAsync(TRGM);
    await _executeAsync("ALTER EXTENSION pg_trgm UPDATE");

    var after = await _provisioner().ProvisionAsync(before, CapabilityProvisionAction.Upgrade, CancellationToken.None);

    await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(after.Version).IsEqualTo(before.AvailableVersion);
  }

  [Test]
  public async Task Upgrade_RefusedToARoleThatDoesNotOwnIt_StaysReadyAndPresent_WithTheRefusalAsync() {
    await _executeAsync("CREATE EXTENSION pg_trgm VERSION '1.3'");
    var role = await _createRoleAsync();
    try {
      var before = await _probeAsync(TRGM, _as(role));

      var after = await _provisioner(_as(role)).ProvisionAsync(before, CapabilityProvisionAction.Upgrade, CancellationToken.None);

      await Assert.That(after.State).IsEqualTo(CapabilityState.Ready)
        .Because("the installed version still works; only the upgrade was refused");
      await Assert.That(after.Version).IsEqualTo("1.3");
      await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
      await Assert.That(after.Reason).StartsWith($"upgrade to {before.AvailableVersion} refused (42501: must be owner of extension pg_trgm");
    } finally {
      await _dropRoleAsync(role);
    }
  }

  [Test]
  public async Task Create_ForARoleTheProbeFindsWithoutPrivilege_ReturnsNoPrivilege_WithoutTryingOrThrowingAsync() {
    var before = await _probeAsync(TRGM);
    var role = await _createRoleAsync();
    try {
      var after = await _provisioner(_as(role)).ProvisionAsync(before, CapabilityProvisionAction.Create, CancellationToken.None);

      await Assert.That(after.State).IsEqualTo(CapabilityState.NoPrivilege);
      await Assert.That(after.Detail).IsEqualTo(nameof(PostgresExtensionState.NoPrivilege));
      await Assert.That(after.Provisioning).IsNull();
    } finally {
      await _dropRoleAsync(role);
    }
  }

  [Test]
  public async Task Create_RefusedByTheServer_ReturnsTheStateTheRefusalReveals_WithoutThrowingAsync() {
    // The allowlist makes the probe believe an untrusted extension is creatable by a role with CREATE on the
    // database, as on a managed server; this server has no such administrator role, so CREATE refuses.
    var role = await _createRoleAsync($"GRANT CREATE ON DATABASE \"{_database.Name}\" TO @role;");
    const string allowlist = "SET azure.extensions = 'PGSTATTUPLE'";
    try {
      var before = await _probeAsync("pgstattuple", _as(role), allowlist);

      var after = await _provisioner(_as(role), allowlist)
        .ProvisionAsync(before, CapabilityProvisionAction.Create, CancellationToken.None);

      await Assert.That(before.State).IsEqualTo(CapabilityState.Provisionable);
      await Assert.That(after.State).IsEqualTo(CapabilityState.NoPrivilege);
      await Assert.That(after.Detail).IsEqualTo("42501");
      await Assert.That(after.Reason).IsNotNull();
      await Assert.That(after.Provisioning).IsNull();
    } finally {
      await _dropRoleAsync(role);
    }
  }

  [Test]
  public async Task Create_WaitsForTheSchemaLock_ThenCreatesAsync() {
    var before = await _probeAsync(TRGM);
    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync();
    var transaction = await holder.BeginTransactionAsync();
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, transaction)) {
      take.Parameters.AddWithValue("key", SchemaInitializationLockKey.Compute("public"));
      await take.ExecuteNonQueryAsync();
    }
    var signal = new LockWaitSignal();

    var provisioning = _provisioner(logger: signal).ProvisionAsync(before, CapabilityProvisionAction.Create, CancellationToken.None);
    await signal.Waiting.Task;
    var whileHeld = (await _probeAsync(TRGM)).State;
    await transaction.CommitAsync();
    var after = await provisioning;

    await Assert.That(whileHeld).IsEqualTo(CapabilityState.Provisionable)
      .Because("nothing is created while another session holds the schema lock");
    await Assert.That(after.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Created);
  }

  [Test]
  public async Task ThroughTheStep_ProvisioningOff_AnOperatorCreatedExtensionIsReady_AndAnAvailableOneIsNotCreatedAsync() {
    await _executeAsync("CREATE EXTENSION pg_trgm");
    var (registry, provisioned) = await _stepAsync(autoProvision: false, _explicitlyRequires(TRGM), _explicitlyRequires("pgstattuple"));

    var trgm = registry.Verdict.Entries.Single(e => e.Status.Descriptor.Id.Name == TRGM);
    var stattuple = registry.Verdict.Entries.Single(e => e.Status.Descriptor.Id.Name == "pgstattuple");
    await Assert.That(trgm.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
    await Assert.That(trgm.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(stattuple.Status.State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(stattuple.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail);
    await Assert.That(provisioned).DoesNotContain("pgstattuple");
  }

  [Test]
  public async Task ThroughTheStep_AnExplicitRequirementOnAnAvailableExtension_IsCreatedAndPassesAsync() {
    var (registry, provisioned) = await _stepAsync(autoProvision: true, _explicitlyRequires(TRGM));

    var entry = registry.Verdict.Entries.Single();
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
    await Assert.That(entry.Line).StartsWith($"capability postgres/pg_trgm on '{SCOPE}': Ready, created ");
    await Assert.That(provisioned).Contains(TRGM);
  }

  private static ActiveCapabilityRequirement _explicitlyRequires(string extension) =>
    new(new CapabilityRequirement(new CapabilityId(PostgresExtensionCatalog.PROVIDER, extension), $"Uses {extension}",
      $"Tests:{extension}", "true", Default: null), $"Tests:{extension}", "true", CapabilityActivation.Explicit);

  private async Task<(ICapabilityRegistry Registry, IReadOnlyList<string> Installed)> _stepAsync(
      bool autoProvision, params ActiveCapabilityRequirement[] requirements) {
    var options = new CapabilityOptions { AutoProvision = autoProvision };
    var registry = new CapabilityRegistry([new PostgresCapabilityProvider([_db()])], NullLogger<CapabilityRegistry>.Instance);
    var step = new CapabilitiesStartupStep(registry, Options.Create(options), [new FixedCapabilityRequirementSource(requirements)],
      [_provisioner()], configuration: null, NullLogger<CapabilitiesStartupStep>.Instance);

    await step.ExecuteAsync(CancellationToken.None);

    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT extname FROM pg_extension", connection);
    var installed = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      installed.Add(reader.GetString(0));
    }
    return (registry, installed);
  }
}
