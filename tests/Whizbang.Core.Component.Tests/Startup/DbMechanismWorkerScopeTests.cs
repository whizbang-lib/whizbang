// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A worker that names its mechanism is started inside it, so its loop carries the name to every statement without
/// opening a scope itself; the host's own flow is left as it was. A worker that names none runs with none, and its
/// statements report as untagged.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/GatedHostedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/IDbMechanismOwner.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
[Category("Startup")]
public class DbMechanismWorkerScopeTests {
  [Test]
  [Timeout(30000)]
  public async Task AWorkerThatNamesItsMechanism_RunsItsLoopInsideIt_AndTheHostFlowIsUnchangedAsync(CancellationToken cancellationToken) {
    var inner = new NamedLoop(DbMechanism.IntegrityAudit);
    using var wrapper = new StartupStageGatedService<NamedLoop>(inner, StartupStage.Coordinate, gate: null);

    await wrapper.StartAsync(cancellationToken);
    var seen = await inner.SeenInLoop.WaitAsync(cancellationToken);

    await Assert.That(seen).IsSameReferenceAs(DbMechanism.IntegrityAudit)
      .Because("the loop runs past its first await with the mechanism the host opened around its start");
    await Assert.That(DbMechanismScope.Current).IsNull().Because("the host's own flow never carries a worker's mechanism");
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AWorkerThatNamesNone_RunsWithNoneAsync(CancellationToken cancellationToken) {
    var inner = new LoopRecorder();
    using var wrapper = new StartupStageGatedService<LoopRecorder>(inner, StartupStage.Coordinate, gate: null);

    await wrapper.StartAsync(cancellationToken);
    var seen = await inner.SeenInLoop.WaitAsync(cancellationToken);

    await Assert.That(seen).IsNull();
    await wrapper.StopAsync(cancellationToken);
  }

  /// <summary>Records the mechanism its loop sees after its first await.</summary>
  private class LoopRecorder : BackgroundService {
    private readonly TaskCompletionSource<DbMechanism?> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<DbMechanism?> SeenInLoop => _seen.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      await Task.Yield();
      _seen.TrySetResult(DbMechanismScope.Current);
    }
  }

  /// <summary>A recorder that names its mechanism.</summary>
  private sealed class NamedLoop(DbMechanism mechanism) : LoopRecorder, IDbMechanismOwner {
    public DbMechanism DbMechanism { get; } = mechanism;
  }
}
