// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The forced pass arrives at a TIME after the first deferral, not after a count of ticks.
/// </summary>
/// <remarks>
/// <para>
/// A ceiling counted in ticks is really the count times whatever the tick happens to be, so an
/// operator who shortens the maintenance interval to clean up more often silently shortens the
/// longest stretch cleanup can be held back, and one who lengthens it silently stretches that
/// stretch to many hours. <c>Whizbang:Housekeeping:MaxDeferral</c> states the ceiling directly.
/// </para>
/// <para>
/// Precedence: an explicit <c>MaxDeferral</c> governs. Without it, an explicitly configured
/// <c>MaxConsecutiveDeferrals</c> keeps its legacy count behavior. With neither, the default
/// one-hour time ceiling applies.
/// </para>
/// <para>
/// Every elapsed-time assertion runs on a fake clock and advances it explicitly; nothing here
/// waits.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingSettingsValidator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration</docs>
[Category("Workers")]
public class HousekeepingDeferralCeilingTests {

  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static ServiceBacklog _busy() => new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 1 };

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build(HousekeepingCoordinator.Settings settings) {
    var clock = new FakeTimeProvider(_origin);
    return (new HousekeepingCoordinator(settings, clock), clock);
  }

  private static HousekeepingCoordinator.Decision _maintenance(HousekeepingCoordinator coordinator)
    => coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

  // ---- The ceiling is a time. ----

  [Test]
  public async Task AShortTick_DoesNotForceTheSweepBeforeMaxDeferralAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxDeferral = TimeSpan.FromMinutes(30),
    });

    // One request a minute: thirty of them, from the first deferral up to a minute short of the ceiling.
    for (var minute = 0; minute < 30; minute++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse()
        .Because($"minute {minute} is inside the ceiling; a counted ceiling would have forced the sweep "
          + "on the seventh request no matter how short the tick");
      clock.Advance(TimeSpan.FromMinutes(1));
    }

    var forced = _maintenance(coordinator);

    await Assert.That(forced.Granted).IsTrue();
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("thirty minutes after the first deferral is the configured ceiling, whatever the tick");
  }

  [Test]
  public async Task TheSweepIsNotForcedOneMomentBeforeMaxDeferral_AndIsForcedAtItAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxDeferral = TimeSpan.FromHours(1),
    });

    _ = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
    var early = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromSeconds(1));
    var atCeiling = _maintenance(coordinator);

    await Assert.That(early.Granted).IsFalse()
      .Because("one second short of the ceiling is still inside it");
    await Assert.That(early.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
    await Assert.That(atCeiling.Granted).IsTrue()
      .Because("the ceiling is reached when the time since the first deferral equals MaxDeferral, "
        + "however few ticks that took");
    await Assert.That(atCeiling.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task WithNothingConfigured_TheCeilingIsOneHourAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero });

    for (var minute = 0; minute < 60; minute++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse()
        .Because($"minute {minute} is inside the default one-hour ceiling");
      clock.Advance(TimeSpan.FromMinutes(1));
    }

    var forced = _maintenance(coordinator);

    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the default ceiling is a time, so a host that never configured housekeeping gets the "
        + "same hour whatever its maintenance interval");
  }

  [Test]
  public async Task TheDefaultMaxDeferral_IsOneHourAsync()
    => await Assert.That(new HousekeepingCoordinator.Settings().MaxDeferral).IsEqualTo(TimeSpan.FromHours(1));

  [Test]
  public async Task TheWindowRestartsAfterASweepActuallyRunsAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxDeferral = TimeSpan.FromMinutes(10),
    });

    _ = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(10));
    var forced = _maintenance(coordinator);
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
    coordinator.End(HousekeepingCoordinator.Activity.Maintenance);

    var restarted = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(5));
    var midWindow = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(5));
    var forcedAgain = _maintenance(coordinator);

    await Assert.That(restarted.Granted).IsFalse()
      .Because("a sweep that ran re-arms the ceiling; keeping the old first-deferral time would force "
        + "every later request, which is the un-gated behavior the ceiling bounds");
    await Assert.That(midWindow.Granted).IsFalse()
      .Because("the new window started at the first deferral after the sweep, five minutes ago");
    await Assert.That(forcedAgain.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task DeadLetterRecovery_HasItsOwnTimeWindowAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxDeferral = TimeSpan.FromMinutes(10),
    });

    _ = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(5));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    clock.Advance(TimeSpan.FromMinutes(5));
    var maintenance = _maintenance(coordinator);
    var recoveryEarly = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    clock.Advance(TimeSpan.FromMinutes(5));
    var recovery = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());

    await Assert.That(maintenance.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
    await Assert.That(recoveryEarly.Granted).IsFalse()
      .Because("recovery first deferred five minutes after cleanup did, so its window has five minutes left");
    await Assert.That(recovery.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("recovery spends the same time ceiling from its own first deferral");
  }

  // ---- Precedence over the legacy count. ----

  [Test]
  public async Task WithOnlyTheCountConfigured_TheCountStillGovernsAsync() {
    var (coordinator, clock) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxConsecutiveDeferrals = 3,
    });

    for (var i = 0; i < 3; i++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse();
      // Hours between requests: a time ceiling would have forced long before the fourth.
      clock.Advance(TimeSpan.FromHours(2));
    }
    var forced = _maintenance(coordinator);

    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("a host that configured only the count keeps the behavior it configured");
  }

  [Test]
  public async Task WithBothConfigured_TheTimeGovernsAsync() {
    var (coordinator, _) = _build(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxConsecutiveDeferrals = 2,
      MaxDeferral = TimeSpan.FromHours(1),
    });

    for (var i = 0; i < 10; i++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse()
        .Because("an explicit MaxDeferral governs; the count is a fallback only when the time is unset");
    }
  }

  // ---- The forced pass is still reported as one. ----

  [Test]
  public async Task AForcedPassByTime_IsCountedAsADeferralLimitVerdictAsync() {
    var metrics = new HousekeepingMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()),
      idleTracker: null);
    // The decisions counter is passive: it reports a cumulative total per tag set when observed.
    var totals = new Dictionary<string, long>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == metrics.Decisions.Meter && instrument.Name == "whizbang.housekeeping.decisions") {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? activity = null, verdict = null;
      foreach (var tag in tags) {
        if (tag.Key == "activity") { activity = tag.Value?.ToString(); }
        if (tag.Key == "verdict") { verdict = tag.Value?.ToString(); }
      }
      lock (totals) { totals[$"{activity}/{verdict}"] = value; }
    });
    listener.Start();
    var clock = new FakeTimeProvider(_origin);
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero, MaxDeferral = TimeSpan.FromMinutes(15) },
      metrics,
      clock);

    _ = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(15));
    _ = _maintenance(coordinator);
    listener.RecordObservableInstruments();

    long busy, forced;
    lock (totals) {
      busy = totals.GetValueOrDefault("Maintenance/ServiceBusy");
      forced = totals.GetValueOrDefault("Maintenance/ProceedDeferralLimit");
    }
    await Assert.That(busy).IsEqualTo(1);
    await Assert.That(forced).IsEqualTo(1)
      .Because("whizbang.housekeeping.decisions by verdict is how an operator sees that forced passes "
        + "arrive at MaxDeferral after the first deferral");
  }

  // ---- Configuration reaches the mechanism. ----

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings, FakeTimeProvider clock) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(clock);
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task MaxDeferral_BindsFromItsConfigurationKeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Housekeeping:MaxDeferral"] = "00:20:00",
    }, new FakeTimeProvider(_origin));

    var settings = provider.GetRequiredService<IOptions<HousekeepingCoordinator.Settings>>().Value;

    await Assert.That(settings.MaxDeferral).IsEqualTo(TimeSpan.FromMinutes(20));
  }

  [Test]
  public async Task ConfiguredMaxDeferral_ReachesTheCoordinator_OnTheRegisteredClockAsync() {
    var clock = new FakeTimeProvider(_origin);
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Housekeeping:MaxDeferral"] = "00:20:00",
    }, clock);
    var coordinator = provider.GetRequiredService<HousekeepingCoordinator>();

    var first = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
    var early = _maintenance(coordinator);
    clock.Advance(TimeSpan.FromSeconds(1));
    var forced = _maintenance(coordinator);

    await Assert.That(first.Granted).IsFalse();
    await Assert.That(early.Granted).IsFalse();
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the configured ceiling must govern the coordinator the workers consult, measured on "
        + "the clock the host registered, or the knob is scenery");
  }

  [Test]
  public async Task WithNoHousekeepingConfiguration_TheTurnkeyCoordinatorUsesTheOneHourCeilingAsync() {
    var clock = new FakeTimeProvider(_origin);
    await using var provider = _hostWith([], clock);
    var coordinator = provider.GetRequiredService<HousekeepingCoordinator>();

    for (var i = 0; i < 10; i++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse()
        .Because("binding an empty section must not read as an explicitly configured count");
    }
    clock.Advance(TimeSpan.FromHours(1));
    var forced = _maintenance(coordinator);

    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task WithBothKeysConfigured_TheTurnkeyCoordinatorUsesTheTimeAsync() {
    var clock = new FakeTimeProvider(_origin);
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Housekeeping:MaxConsecutiveDeferrals"] = "1",
      ["Whizbang:Housekeeping:MaxDeferral"] = "01:00:00",
    }, clock);
    var coordinator = provider.GetRequiredService<HousekeepingCoordinator>();

    for (var i = 0; i < 5; i++) {
      var refused = _maintenance(coordinator);
      await Assert.That(refused.Granted).IsFalse()
        .Because("MaxDeferral takes precedence over the legacy count");
    }
  }

  // ---- Validation. ----

  [Test]
  [Arguments(0)]
  [Arguments(-60)]
  public async Task ANonPositiveMaxDeferral_IsRejectedByNameAsync(int seconds) {
    var settings = new HousekeepingCoordinator.Settings { MaxDeferral = TimeSpan.FromSeconds(seconds) };

    var result = new HousekeepingSettingsValidator().Validate(null, settings);

    await Assert.That(result.Failed).IsTrue()
      .Because("a ceiling of zero or less forces every deferred sweep through at once, which switches the idle gate off");
    await Assert.That(result.FailureMessage).Contains("Whizbang:Housekeeping:MaxDeferral")
      .Because("the message names the key the operator has to fix");
  }

  [Test]
  public async Task APositiveMaxDeferral_AndTheDefaults_AreAcceptedAsync() {
    var validator = new HousekeepingSettingsValidator();

    await Assert.That(validator.Validate(null, new HousekeepingCoordinator.Settings()).Succeeded).IsTrue();
    await Assert.That(validator.Validate(
      null, new HousekeepingCoordinator.Settings { MaxDeferral = TimeSpan.FromMinutes(5) }).Succeeded).IsTrue();
  }

  [Test]
  public async Task TheValidator_RejectsANullSettingsInstanceAsync()
    => await Assert.That(() => new HousekeepingSettingsValidator().Validate(null, null!)).Throws<ArgumentNullException>();

  [Test]
  public async Task TheTurnkeyPipeline_RejectsANonPositiveMaxDeferralAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Housekeeping:MaxDeferral"] = "00:00:00",
    }, new FakeTimeProvider(_origin));

    await Assert.That(() => provider.GetRequiredService<IOptions<HousekeepingCoordinator.Settings>>().Value)
      .Throws<OptionsValidationException>()
      .Because("the validator is registered with the coordinator, so a zero ceiling fails where it is read");
  }
}
