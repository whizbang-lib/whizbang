// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Component.Tests.Schema;

/// <summary>
/// The schema initializer is one runner for every driver: each driver registers what it initializes, and the
/// initializer runs every registration in order, then opens the schema-ready gate, exactly once.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ISchemaInitializationRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationRegistration.cs</code-under-test>
public class SchemaInitializationRunnersTests {

  [Test]
  public async Task Start_RunsEveryRegisteredRunnerInOrder_ThenOpensTheGateAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var first = new RecordingRunner("first", order, gate);
    var second = new RecordingRunner("second", order, gate);
    var service = _create(gate, first, second);

    await service.StartAsync(CancellationToken.None);

    await Assert.That(order).IsEquivalentTo(["first", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(first.GateWasOpenWhenItRan || second.GateWasOpenWhenItRan).IsFalse()
      .Because("the gate opens after the last runner, so nothing reads a schema a later runner has not finished");
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task Start_WhenARunnerFails_TheLaterOnesDoNotRun_AndTheGateStaysClosedAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var failing = new RecordingRunner("failing", order, gate, new InvalidOperationException("migration failed"));
    var later = new RecordingRunner("later", order, gate);
    var service = _create(gate, failing, later);

    await Assert.That(async () => await service.StartAsync(CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(order).IsEquivalentTo(["failing"]);
    await Assert.That(gate.IsReady).IsFalse();
  }

  [Test]
  public async Task Background_AFailedAttempt_IsReportedToEveryObserver_ThenRetriedAsync() {
    var gate = new SchemaReadyGate();
    var attempts = 0;
    var flaky = new FlakyRunner(() => ++attempts == 1);
    var watching = new FailureRecorder();
    var service = new WhizbangDatabaseInitializerService(
      new ServiceCollection().BuildServiceProvider(),
      [flaky],
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = true, InitRetryDelay = TimeSpan.Zero }),
      TimeProvider.System,
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      [new SilentObserver(), watching]);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;

    await Assert.That(watching.Attempts).IsEquivalentTo([1]);
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task Registration_OpensTheRegisteredGate_WhenNoDriverRegisteredAnythingAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangSchemaInitialization();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);

    await using var provider = services.BuildServiceProvider();
    var initializer = provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single();
    await initializer.StartAsync(CancellationToken.None);

    await Assert.That(provider.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue()
      .Because("the gate the initializer opens is the registered one the workers wait on");
  }

  private static WhizbangDatabaseInitializerService _create(ISchemaReadyGate gate, params ISchemaInitializationRunner[] runners) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    return new WhizbangDatabaseInitializerService(
      services.BuildServiceProvider(),
      runners,
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = false }),
      TimeProvider.System,
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);
  }

  private sealed class FlakyRunner(Func<bool> fails) : ISchemaInitializationRunner {
    public Task RunAsync(CancellationToken cancellationToken) =>
      fails() ? Task.FromException(new InvalidOperationException("the database is not up yet")) : Task.CompletedTask;
  }

  /// <summary>Watches nothing; every notification takes its default.</summary>
  private sealed class SilentObserver : ISchemaInitializationObserver;

  private sealed class FailureRecorder : ISchemaInitializationObserver {
    public List<int> Attempts { get; } = [];

    public ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) {
      Attempts.Add(attempt);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingRunner(string name, List<string> order, ISchemaReadyGate gate, Exception? failure = null)
    : ISchemaInitializationRunner {
    public bool GateWasOpenWhenItRan { get; private set; }

    public Task RunAsync(CancellationToken cancellationToken) {
      GateWasOpenWhenItRan = gate.IsReady;
      order.Add(name);
      return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
  }
}
