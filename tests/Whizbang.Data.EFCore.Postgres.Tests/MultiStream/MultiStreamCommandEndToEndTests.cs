// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.MultiStream;

namespace Whizbang.Data.EFCore.Postgres.Tests.MultiStream;

/// <summary>
/// Two instances, one database (#1358): a command naming forty streams is taken in once, and each stream is worked
/// exactly once, by the instance that owns it, and the run completes exactly once. The waits are on the run's completion
/// and on each worker's passes; the poll interval is an hour, so the work is driven by the requests below.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands</docs>
[Category("Shard3")]
[NotInParallel("MultiStreamEndToEnd")]
public class MultiStreamCommandEndToEndTests : EFCoreTestBase {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(60);

  [Test]
  public async Task ACommandOverManyStreams_IsWorkedOncePerStreamEachOnItsOwnerAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var version = await _assignAsync(conn, [a, b]);
    var streams = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();
    var worked = new ConcurrentBag<(Guid Instance, Guid Stream)>();
    var completions = new ConcurrentBag<MultiStreamRunCompletion>();
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var workerA = _worker(a, version, worked);
    var workerB = _worker(b, version, worked);
    foreach (var worker in new[] { workerA, workerB }) {
      worker.OnRunCompleted += c => {
        completions.Add(c);
        completed.TrySetResult();
      };
    }
    var run = Guid.CreateVersion7();

    await new MultiStreamCommandIntake<EndToEndCommand>(_store(), NullLogger<MultiStreamCommandIntake<EndToEndCommand>>.Instance)
      .HandleAsync(new EndToEndCommand(run, streams, "reindex"));
    await workerA.StartAsync(CancellationToken.None);
    await workerB.StartAsync(CancellationToken.None);
    try {
      workerA.RequestImmediateRun();
      workerB.RequestImmediateRun();
      await completed.Task.WaitAsync(_deadline);
    } finally {
      await workerA.StopAsync(CancellationToken.None);
      await workerB.StopAsync(CancellationToken.None);
    }

    await Assert.That(worked.Select(w => w.Stream).Order()).IsEquivalentTo(streams.Order(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every stream is worked, and none twice");
    foreach (var (instance, stream) in worked) {
      var partition = await _partitionAsync(conn, stream);
      await Assert.That(instance).IsEqualTo(partition % 2 == 0 ? a : b)
        .Because($"stream {stream} (partition {partition}) belongs to the instance that owns its partition");
    }
    await Assert.That(worked.Select(w => w.Instance).Distinct().Count()).IsEqualTo(2)
      .Because("forty streams fall in both instances' shares, so both worked some");
    await Assert.That(completions.Single()).IsEqualTo(
      new MultiStreamRunCompletion(run, Whizbang.Core.TypeNameFormatter.Format(typeof(EndToEndCommand)), 40, 40, 0));
    var status = await _store().ReadRunAsync(run, CancellationToken.None);
    await Assert.That(status!.Pending).IsEqualTo(0);
    await Assert.That(status.CompletedAt).IsNotNull();
  }

  public sealed record EndToEndCommand(Guid RunId, IReadOnlyList<Guid> StreamIds, string Purpose) : IMultiStreamCommand;

  public sealed class EndToEndHandler(EndToEndRecorder recorder) : IMultiStreamCommandHandler<EndToEndCommand> {
    public ValueTask HandleStreamAsync(EndToEndCommand command, Guid streamId, CancellationToken cancellationToken) {
      recorder.Worked.Add((recorder.Instance, streamId));
      return ValueTask.CompletedTask;
    }
  }

  public sealed record EndToEndRecorder(Guid Instance, ConcurrentBag<(Guid Instance, Guid Stream)> Worked);

  private MultiStreamCommandWorker _worker(Guid instance, PartitionAssignmentVersion version, ConcurrentBag<(Guid, Guid)> worked) {
    var services = new ServiceCollection();
    services.AddMultiStreamCommandHandler<EndToEndCommand, EndToEndHandler>();
    services.AddSingleton(new EndToEndRecorder(instance, worked));
    var provider = services.BuildServiceProvider();
    return new MultiStreamCommandWorker(
      _store(),
      provider.GetServices<IMultiStreamCommandBinding>(),
      provider.GetRequiredService<IServiceScopeFactory>(),
      new ServiceInstanceProvider(instance, "e2e", "e2e-host", 1),
      Options.Create(new MultiStreamCommandOptions { BatchSize = 7, PollInterval = TimeSpan.FromHours(1) }),
      NullLogger<MultiStreamCommandWorker>.Instance,
      new FixedAssignment(version),
      SchemaReadyGate.AlreadyReady());
  }

  private PgMultiStreamCommandStore _store() =>
    new(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build());

  private static (Guid A, Guid B) _twoInstancesInIdOrder() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    return string.CompareOrdinal(first.ToString(), second.ToString()) < 0 ? (first, second) : (second, first);
  }

  private static async Task<int> _partitionAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT compute_partition('{stream}'::uuid, 10000)";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<PartitionAssignmentVersion> _assignAsync(NpgsqlConnection conn, Guid[] members) {
    foreach (var member in members) {
      await using var heartbeat = conn.CreateCommand();
      heartbeat.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, @mode)";
      heartbeat.Parameters.AddWithValue("inst", member);
      heartbeat.Parameters.AddWithValue("mode", InstanceConnectionModes.POOLED);
      await heartbeat.ExecuteNonQueryAsync();
    }
    await using var vote = conn.CreateCommand();
    vote.CommandText = $"SELECT epoch FROM wh_vote_role('{PartitionAssignerOptions.ROLE}', '{members[0]}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'";
    var epoch = Convert.ToInt64(await vote.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    await using var publish = conn.CreateCommand();
    publish.CommandText = "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')";
    publish.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    publish.Parameters.AddWithValue("inst", members[0]);
    publish.Parameters.AddWithValue(nameof(epoch), epoch);
    publish.Parameters.AddWithValue(nameof(members), members);
    var revision = Convert.ToInt64(await publish.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return new PartitionAssignmentVersion(epoch, revision);
  }

  private sealed class FixedAssignment(PartitionAssignmentVersion version) : IPartitionAssignmentSource {
    public PartitionAssignment? Current => null;
    public event Action<PartitionAssignment?>? OnAssignmentChanged;
    public void MarkStale() => OnAssignmentChanged?.Invoke(null);
    public ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken) =>
      ValueTask.FromResult<PartitionAssignmentVersion?>(version);
  }
}
