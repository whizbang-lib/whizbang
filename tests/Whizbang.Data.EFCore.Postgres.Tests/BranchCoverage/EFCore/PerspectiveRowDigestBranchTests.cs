// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCorePostgresPerspectiveRowDigest"/>'s table resolution: a model
/// this service has not registered a table for, and a registered table name that is not a safe
/// generated identifier, both answer "no digest" instead of guessing or interpolating it into SQL.
/// Runs beside the sibling digest suite so the resolving arms are measured in the same shard.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/EFCorePostgresPerspectiveRowDigest.cs</code-under-test>
[Category("Shard3")]
public class PerspectiveRowDigestBranchTests : EFCoreTestBase {

  private const string SERVICE = "digest-branch-tests";

  [Test]
  public async Task ModelWithNoRegisteredTable_YieldsNoDigestAsync() {
    var digest = _digest("Unregistered.Projection", "Model.Unregistered");

    var result = await digest.ComputeAsync("Unregistered.Projection", [Guid.NewGuid()], CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("with no registry row for this service there is no table to digest");
  }

  [Test]
  public async Task RegisteredTableNameThatIsNotASafeIdentifier_YieldsNoDigestAsync() {
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
        VALUES ('Model.Unsafe', 'Unsafe-Table; DROP TABLE x', '{}'::jsonb, 'hash', @service)
        """;
      cmd.Parameters.AddWithValue("service", SERVICE);
      await cmd.ExecuteNonQueryAsync();
    }
    var digest = _digest("Unsafe.Projection", "Model.Unsafe");

    var result = await digest.ComputeAsync("Unsafe.Projection", [Guid.NewGuid()], CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("a name outside the generated-identifier shape is never interpolated into SQL");
  }

  private EFCorePostgresPerspectiveRowDigest _digest(string perspectiveName, string modelType) =>
    new(new WorkCoordinationDbContext(DbContextOptions), new OneEntryRegistry(perspectiveName, modelType), new FixedServiceInstance(SERVICE));

  private sealed class FixedServiceInstance(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class OneEntryRegistry(string clrTypeName, string modelType) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(clrTypeName, clrTypeName, modelType, [])];

    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();

    public IReadOnlyList<Type> GetEventTypes() => [];
  }
}
