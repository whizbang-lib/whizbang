// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Branch coverage for <see cref="ScheduleWorker"/>: the options guard (a null accessor and an
/// accessor yielding a null value both fail construction naming <c>options</c>), and disposal on a
/// host with no signal bus configured (no doorbell subscription to release, the timer still torn down).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Temporal/ScheduleWorker.cs</code-under-test>
public class ScheduleWorkerBranchCoverageTests {
  private sealed class NullValueOptions : IOptions<TemporalOptions> {
    public TemporalOptions Value => null!;
  }

  private static ScheduleWorker _create(IOptions<TemporalOptions> options, TimeProvider? clock = null) {
    var provider = new ServiceCollection().BuildServiceProvider();
    return new ScheduleWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: options,
      logger: NullLogger<ScheduleWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      loggerFactory: NullLoggerFactory.Instance,
      signalBus: NullSignalBus.Instance,
      timeProvider: clock);
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullNamingOptionsAsync() {
    var ex = await Assert.That(() => _create(null!)).Throws<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullNamingOptionsAsync() {
    var ex = await Assert.That(() => _create(new NullValueOptions())).Throws<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("options")
      .Because("an options accessor that yields no value is as unusable as no accessor at all");
  }

  [Test]
  public async Task Dispose_NoSignalBusConfigured_StillDisposesTheTimerAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var worker = _create(Options.Create(new TemporalOptions()), clock);
    var timer = worker.TimerForTests;

    worker.Dispose();

    // A disposed ScheduleTimer ignores ArmFor; a live one records the arming.
    timer.ArmFor(clock.GetUtcNow().AddSeconds(30));
    await Assert.That(timer.ArmedFor).IsNull()
      .Because("with no doorbell subscription to release, disposal must still reach and dispose the timer");
  }
}
