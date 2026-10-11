// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the blocking schema initialization's migration ceiling: a cancellation the
/// ceiling caused becomes a <see cref="TimeoutException"/>; a cancellation caused by host shutdown,
/// alone or together with the ceiling, is shutdown and propagates as cancellation. Driven with a
/// fake clock and a runner that throws only when released, so the ordering is deterministic.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
[Category("Shard3")]
public class InitializerMigrationTimeoutBranchTests {

  private static readonly TimeSpan _ceiling = TimeSpan.FromMinutes(5);

  [Test]
  public async Task CeilingOnly_SurfacesAsATimeoutAsync() {
    var clock = new FakeTimeProvider();
    var runner = new ReleasedRunner();
    using var host = new CancellationTokenSource();
    var start = _create(runner, clock).StartAsync(host.Token);
    await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    clock.Advance(_ceiling + TimeSpan.FromSeconds(1));
    runner.Release();

    await Assert.That(async () => await start).Throws<TimeoutException>()
      .Because("the migration blew its ceiling while the host was still starting");
  }

  [Test]
  public async Task HostShutdownOnly_PropagatesAsCancellationAsync() {
    var clock = new FakeTimeProvider();
    var runner = new ReleasedRunner();
    using var host = new CancellationTokenSource();
    var start = _create(runner, clock).StartAsync(host.Token);
    await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await host.CancelAsync();
    runner.Release();

    await Assert.That(async () => await start).Throws<OperationCanceledException>()
      .Because("shutdown during migration is not a timeout and must not be reported as one");
  }

  [Test]
  public async Task CeilingAndHostShutdownTogether_IsShutdownNotATimeoutAsync() {
    var clock = new FakeTimeProvider();
    var runner = new ReleasedRunner();
    using var host = new CancellationTokenSource();
    var start = _create(runner, clock).StartAsync(host.Token);
    await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    clock.Advance(_ceiling + TimeSpan.FromSeconds(1));
    await host.CancelAsync();
    runner.Release();

    await Assert.That(async () => await start).Throws<OperationCanceledException>()
      .Because("when the host is shutting down, shutdown wins over the ceiling");
  }

  private static WhizbangDatabaseInitializerService _create(ISchemaInitializationRunner runner, TimeProvider clock) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    var provider = services.BuildServiceProvider();
    return new WhizbangDatabaseInitializerService(
      provider,
      [runner],
      new SchemaReadyGate(),
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = false, MigrationTimeout = _ceiling }),
      clock,
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);
  }

  /// <summary>
  /// Signals when the migration starts, then throws a cancellation of its own once released,
  /// regardless of its token, so the test decides which sources had fired at that moment.
  /// </summary>
  private sealed class ReleasedRunner : ISchemaInitializationRunner {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public async Task RunAsync(CancellationToken cancellationToken) {
      Entered.TrySetResult();
      await _release.Task;
      throw new OperationCanceledException(cancellationToken);
    }
  }
}
