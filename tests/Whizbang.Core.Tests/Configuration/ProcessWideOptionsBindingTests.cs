// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Health;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.RunControl;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// #1014: the process-wide options classes bind from configuration, so an operator can change them at
/// deploy time without a code change. Each test sets every documented key of one section to a
/// non-default value and proves it reaches the instance the framework resolves.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/ProcessWideOptionsBinding.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference</docs>
[Category("Shard2")]
public sealed class ProcessWideOptionsBindingTests {

  private static ServiceProvider _workers(Dictionary<string, string?> settings) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  private static ServiceProvider _withConfiguration(Dictionary<string, string?> settings, Action<IServiceCollection> register) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    register(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task RedeliveryPumpOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:Redelivery:MaxInnerEventsPerComposite"] = "11",
      ["Whizbang:Redelivery:MaxEventsPerRequest"] = "22",
      ["Whizbang:Redelivery:MaxBytesPerComposite"] = "33",
      ["Whizbang:Redelivery:SelectPageSize"] = "44",
      ["Whizbang:Redelivery:PublishRetryAttempts"] = "7",
      ["Whizbang:Redelivery:PublishRetryBaseDelayMs"] = "55",
    });

    var options = provider.GetRequiredService<IOptions<RedeliveryPumpOptions>>().Value;

    await Assert.That(options.MaxInnerEventsPerComposite).IsEqualTo(11);
    await Assert.That(options.MaxEventsPerRequest).IsEqualTo(22);
    await Assert.That(options.MaxBytesPerComposite).IsEqualTo(33);
    await Assert.That(options.SelectPageSize).IsEqualTo(44);
    await Assert.That(options.PublishRetryAttempts).IsEqualTo(7);
    await Assert.That(options.PublishRetryBaseDelayMs).IsEqualTo(55);
    await Assert.That(provider.GetRequiredService<RedeliveryPumpOptions>()).IsSameReferenceAs(options)
      .Because("the re-delivery receptor resolves the plain instance, so it must be the bound one");
  }

  [Test]
  public async Task ThrottleRetryOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:ThrottleRetry:MaxAttempts"] = "9",
      ["Whizbang:ThrottleRetry:BaseDelay"] = "00:00:01",
      ["Whizbang:ThrottleRetry:BackoffMultiplier"] = "3.5",
      ["Whizbang:ThrottleRetry:MaxDelay"] = "00:00:20",
    });

    var options = provider.GetRequiredService<IOptions<ThrottleRetryOptions>>().Value;

    await Assert.That(options.MaxAttempts).IsEqualTo(9);
    await Assert.That(options.BaseDelay).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.BackoffMultiplier).IsEqualTo(3.5);
    await Assert.That(options.MaxDelay).IsEqualTo(TimeSpan.FromSeconds(20));
  }

  [Test]
  public async Task StreamRateLimiterOptions_EveryKeyBinds_AndTheInjectedLimiterUsesThemAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:StreamRateLimiter:MaxEventsPerWindow"] = "1",
      ["Whizbang:StreamRateLimiter:WindowDuration"] = "00:10:00",
      ["Whizbang:StreamRateLimiter:CooldownDuration"] = "00:10:00",
      ["Whizbang:StreamRateLimiter:StaleEntryTimeout"] = "00:30:00",
    });

    var options = provider.GetRequiredService<IOptions<StreamRateLimiterOptions>>().Value;
    var limiter = provider.GetRequiredService<StreamRateLimiter>();
    var stream = Guid.NewGuid();

    await Assert.That(options.MaxEventsPerWindow).IsEqualTo(1);
    await Assert.That(options.WindowDuration).IsEqualTo(TimeSpan.FromMinutes(10));
    await Assert.That(options.CooldownDuration).IsEqualTo(TimeSpan.FromMinutes(10));
    await Assert.That(options.StaleEntryTimeout).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(limiter.TryAcquire(stream)).IsTrue();
    await Assert.That(limiter.TryAcquire(stream)).IsFalse()
      .Because("a limit of one per window from configuration throttles the second event; the default of 50 would not");
  }

  [Test]
  public async Task DebuggerAwareClockOptions_EveryKeyBinds_AndTheClockUsesThemAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:DebuggerAwareClock:Mode"] = "Disabled",
      ["Whizbang:DebuggerAwareClock:SamplingInterval"] = "00:00:02",
      ["Whizbang:DebuggerAwareClock:FrozenThreshold"] = "4.5",
    }, services => services.AddWhizbang());

    var options = provider.GetRequiredService<IOptions<DebuggerAwareClockOptions>>().Value;

    await Assert.That(options.Mode).IsEqualTo(DebuggerDetectionMode.Disabled);
    await Assert.That(options.SamplingInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(options.FrozenThreshold).IsEqualTo(4.5);
    await Assert.That(provider.GetRequiredService<IDebuggerAwareClock>().Mode).IsEqualTo(DebuggerDetectionMode.Disabled);
  }

  [Test]
  public async Task StandbyWatcherOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:StandbyWatcher:PollInterval"] = "00:00:07",
      ["Whizbang:StandbyWatcher:ObsolescenceInterval"] = "00:02:00",
      ["Whizbang:StandbyWatcher:RequesterLivenessWindow"] = "00:00:45",
    });

    var options = provider.GetRequiredService<IOptions<StandbyWatcherOptions>>().Value;

    await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(7));
    await Assert.That(options.ObsolescenceInterval).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.RequesterLivenessWindow).IsEqualTo(TimeSpan.FromSeconds(45));
    await Assert.That(provider.GetRequiredService<StandbyWatcherOptions>()).IsSameReferenceAs(options)
      .Because("the watcher takes the plain instance, so it must be the bound one");
  }

  [Test]
  public async Task StandbyWatcherOptions_AHostsOwnInstanceStillWinsAsync() {
    var own = new StandbyWatcherOptions { PollInterval = TimeSpan.FromSeconds(1) };
    await using var provider = _withConfiguration(new() {
      ["Whizbang:StandbyWatcher:PollInterval"] = "00:00:07",
    }, services => {
      services.AddSingleton(own);
      services.AddWhizbangWorkers();
    });

    await Assert.That(provider.GetRequiredService<StandbyWatcherOptions>()).IsSameReferenceAs(own);
  }

  [Test]
  public async Task PerspectiveSnapshotOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:Perspectives:Snapshots:SnapshotEveryNEvents"] = "7",
      ["Whizbang:Perspectives:Snapshots:MaxSnapshotsPerStream"] = "2",
      ["Whizbang:Perspectives:Snapshots:EphemeralSnapshotEveryNEvents"] = "3",
      ["Whizbang:Perspectives:Snapshots:EphemeralMaxSnapshotsPerStream"] = "4",
      ["Whizbang:Perspectives:Snapshots:Enabled"] = "false",
      ["Whizbang:Perspectives:Snapshots:RewindSnapshotIntervalEvents"] = "6",
      ["Whizbang:Perspectives:Snapshots:UpgradePolicy"] = "LazyUpcast",
    });

    var options = provider.GetRequiredService<IOptions<PerspectiveSnapshotOptions>>().Value;

    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(7);
    await Assert.That(options.MaxSnapshotsPerStream).IsEqualTo(2);
    await Assert.That(options.EphemeralSnapshotEveryNEvents).IsEqualTo(3);
    await Assert.That(options.EphemeralMaxSnapshotsPerStream).IsEqualTo(4);
    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.RewindSnapshotIntervalEvents).IsEqualTo(6);
    await Assert.That(options.UpgradePolicy).IsEqualTo(SnapshotUpgradePolicy.LazyUpcast);
  }

  [Test]
  public async Task PerspectiveRewindOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:Perspectives:Rewind:Enabled"] = "false",
      ["Whizbang:Perspectives:Rewind:StartupScanEnabled"] = "false",
      ["Whizbang:Perspectives:Rewind:StartupRewindMode"] = "Background",
      ["Whizbang:Perspectives:Rewind:MaxConcurrentRewinds"] = "9",
      ["Whizbang:Perspectives:Rewind:DebounceWindow"] = "00:00:01",
      ["Whizbang:Perspectives:Rewind:MaxDebounceWindow"] = "00:00:09",
    });

    var options = provider.GetRequiredService<IOptions<PerspectiveRewindOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.StartupScanEnabled).IsFalse();
    await Assert.That(options.StartupRewindMode).IsEqualTo(RewindStartupMode.Background);
    await Assert.That(options.MaxConcurrentRewinds).IsEqualTo(9);
    await Assert.That(options.DebounceWindow).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.MaxDebounceWindow).IsEqualTo(TimeSpan.FromSeconds(9));
  }

  [Test]
  public async Task PerspectiveStreamLockOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:Perspectives:StreamLock:LockTimeout"] = "00:01:00",
      ["Whizbang:Perspectives:StreamLock:KeepAliveInterval"] = "00:00:20",
    });

    var options = provider.GetRequiredService<IOptions<PerspectiveStreamLockOptions>>().Value;

    await Assert.That(options.LockTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(options.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(20));
  }

  [Test]
  public async Task PerspectiveStreamAffinityOptions_EveryKeyBindsAsync() {
    await using var provider = _workers(new() {
      ["Whizbang:Workers:PerspectiveAffinity:IdleEvictionWindow"] = "00:30:00",
      ["Whizbang:Workers:PerspectiveAffinity:SweepInterval"] = "00:00:30",
      ["Whizbang:Workers:PerspectiveAffinity:LongHoldWarning"] = "00:02:00",
    });

    var options = provider.GetRequiredService<IOptions<PerspectiveStreamAffinityOptions>>().Value;

    await Assert.That(options.IdleEvictionWindow).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(options.SweepInterval).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.LongHoldWarning).IsEqualTo(TimeSpan.FromMinutes(2));
  }

  private static Dictionary<string, string?> _systemEventSettings() => new() {
    ["Whizbang:SystemEvents:LocalOnly"] = "false",
    ["Whizbang:SystemEvents:AuditMode"] = "OptIn",
    ["Whizbang:SystemEvents:AuditShipSlideSeconds"] = "3",
    ["Whizbang:SystemEvents:AuditShipMaxDelaySeconds"] = "30",
    ["Whizbang:SystemEvents:AuditShipMaxBatchCount"] = "50",
    ["Whizbang:SystemEvents:AuditPriority"] = "200",
  };

  private static async Task _assertSystemEventsBoundAsync(SystemEventOptions options) {
    await Assert.That(options.LocalOnly).IsFalse();
    await Assert.That(options.AuditMode).IsEqualTo(AuditMode.OptIn);
    await Assert.That(options.AuditShipSlideSeconds).IsEqualTo(3);
    await Assert.That(options.AuditShipMaxDelaySeconds).IsEqualTo(30);
    await Assert.That(options.AuditShipMaxBatchCount).IsEqualTo(50);
    await Assert.That(options.AuditPriority).IsEqualTo(200);
  }

  [Test]
  public async Task SystemEventOptions_EveryKeyBinds_WithoutAddSystemEventsAsync() {
    await using var provider = _workers(_systemEventSettings());

    await _assertSystemEventsBoundAsync(provider.GetRequiredService<IOptions<SystemEventOptions>>().Value);
  }

  [Test]
  public async Task SystemEventOptions_EveryKeyBinds_ThroughAddSystemEventsAsync() {
    await using var provider = _withConfiguration(_systemEventSettings(),
      services => services.AddSystemEvents(o => o.AuditShipSlideSeconds = 99));

    await _assertSystemEventsBoundAsync(provider.GetRequiredService<IOptions<SystemEventOptions>>().Value);
  }

  [Test]
  public async Task SystemEventOptions_CodeValueSurvivesAnAbsentKeyAsync() {
    await using var provider = _withConfiguration([], services => services.AddSystemEvents(o => {
      o.EnableAudit();
      o.AuditShipSlideSeconds = 99;
    }));

    var options = provider.GetRequiredService<IOptions<SystemEventOptions>>().Value;

    await Assert.That(options.AuditShipSlideSeconds).IsEqualTo(99);
    await Assert.That(options.EventAuditEnabled).IsTrue();
  }

  [Test]
  public async Task MessageSecurityOptions_EveryKeyBindsAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:MessageSecurity:AllowAnonymous"] = "true",
      ["Whizbang:MessageSecurity:EnableAuditLogging"] = "false",
      ["Whizbang:MessageSecurity:ValidateCredentials"] = "false",
      ["Whizbang:MessageSecurity:Timeout"] = "00:00:09",
      ["Whizbang:MessageSecurity:PropagateToOutgoingMessages"] = "false",
    }, services => services.AddWhizbangMessageSecurity());

    var options = provider.GetRequiredService<MessageSecurityOptions>();

    await Assert.That(options.AllowAnonymous).IsTrue();
    await Assert.That(options.EnableAuditLogging).IsFalse();
    await Assert.That(options.ValidateCredentials).IsFalse();
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(9));
    await Assert.That(options.PropagateToOutgoingMessages).IsFalse();
  }

  [Test]
  public async Task MessageSecurityOptions_ConfigurationOverridesCode_AndCodeSurvivesAbsentKeysAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:MessageSecurity:AllowAnonymous"] = "false",
    }, services => services.AddWhizbangMessageSecurity(o => {
      o.AllowAnonymous = true;
      o.Timeout = TimeSpan.FromSeconds(1);
      o.ExemptMessageTypes.Add(typeof(string));
    }));

    var options = provider.GetRequiredService<MessageSecurityOptions>();

    await Assert.That(options.AllowAnonymous).IsFalse();
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.ExemptMessageTypes).Contains(typeof(string));
  }

  [Test]
  public async Task WhizbangLifecycleOptions_EveryKeyBindsAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Lifecycle:TransitionAckTimeout"] = "00:01:00",
      ["Whizbang:Lifecycle:FaultRecordWindow"] = "00:00:12",
    }, services => services.AddWhizbangRunControl(o => o.FaultRecordWindow = TimeSpan.FromSeconds(1)));

    var options = provider.GetRequiredService<WhizbangLifecycleOptions>();

    await Assert.That(options.TransitionAckTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(options.FaultRecordWindow).IsEqualTo(TimeSpan.FromSeconds(12));
  }

  [Test]
  public async Task WhizbangHealthOptions_EveryKeyBindsAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Health:Default"] = "Strict",
      ["Whizbang:Health:SourceTimeout"] = "00:00:05",
      ["Whizbang:Health:Components:transport"] = "lenient",
    }, services => services.AddWhizbangManagedHealth());

    var options = provider.GetRequiredService<WhizbangHealthOptions>();

    await Assert.That(options.Default).IsSameReferenceAs(HealthPolicy.Strict);
    await Assert.That(options.SourceTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.PolicyFor("transport")).IsSameReferenceAs(HealthPolicy.Lenient);
    await Assert.That(options.PolicyFor("anything-else")).IsSameReferenceAs(HealthPolicy.Strict);
  }

  [Test]
  public async Task WhizbangHealthOptions_CodeComponentsSurviveAndAbsentKeysChangeNothingAsync() {
    await using var provider = _withConfiguration([], services => services.AddWhizbangManagedHealth(o => {
      o.Default = HealthPolicy.Strict;
      o.Components["db"] = HealthPolicy.Lenient;
    }));

    var options = provider.GetRequiredService<WhizbangHealthOptions>();

    await Assert.That(options.Default).IsSameReferenceAs(HealthPolicy.Strict);
    await Assert.That(options.PolicyFor("db")).IsSameReferenceAs(HealthPolicy.Lenient);
    await Assert.That(options.SourceTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task WhizbangHealthOptions_AnUnknownPolicyNameFailsLoudlyAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Health:Components:db"] = "Strictish",
    }, services => services.AddWhizbangManagedHealth());

    var thrown = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<WhizbangHealthOptions>());

    await Assert.That(thrown.Message).Contains("Whizbang:Health:Components:db");
  }

  private static Dictionary<string, string?> _coreSettings() => new() {
    ["Whizbang:Core:ShutdownDeregistrationTimeout"] = "00:00:40",
    ["Whizbang:Core:EnableTagProcessing"] = "false",
    ["Whizbang:Core:TagProcessingMode"] = "AsLifecycleStage",
    ["Whizbang:Core:DefaultQueryScope"] = "Global",
    ["Whizbang:Core:EmptyStreamIdPolicy"] = "DeadLetter",
    ["Whizbang:Core:MaxMessagePayloadBytes"] = "1024",
    ["Whizbang:Core:MessagePayloadWarningRatio"] = "0.5",
    ["Whizbang:ShowBanner"] = "false",
  };

  private static async Task _assertCoreBoundAsync(WhizbangCoreOptions options) {
    await Assert.That(options.ShutdownDeregistrationTimeout).IsEqualTo(TimeSpan.FromSeconds(40));
    await Assert.That(options.EnableTagProcessing).IsFalse();
    await Assert.That(options.TagProcessingMode).IsEqualTo(TagProcessingMode.AsLifecycleStage);
    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.Global);
    await Assert.That(options.EmptyStreamIdPolicy).IsEqualTo(EmptyStreamIdPolicy.DeadLetter);
    await Assert.That(options.MaxMessagePayloadBytes).IsEqualTo(1024L);
    await Assert.That(options.MessagePayloadWarningRatio).IsEqualTo(0.5);
    await Assert.That(options.ShowBanner).IsFalse();
  }

  [Test]
  public async Task WhizbangCoreOptions_EveryKeyBindsOnTheSingletonAsync() {
    await using var provider = _withConfiguration(_coreSettings(), services => services.AddWhizbang());

    await _assertCoreBoundAsync(provider.GetRequiredService<WhizbangCoreOptions>());
  }

  [Test]
  public async Task WhizbangCoreOptions_EveryKeyBindsOnTheOptionsViewAsync() {
    await using var provider = _withConfiguration(_coreSettings(), services => services.AddWhizbang());

    await _assertCoreBoundAsync(provider.GetRequiredService<IOptions<WhizbangCoreOptions>>().Value);
  }

  [Test]
  public async Task WhizbangCoreOptions_ConfigurationOverridesCode_AndCodeSurvivesAbsentKeysAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Core:DefaultQueryScope"] = "Organization",
    }, services => services.AddWhizbang(o => {
      o.DefaultQueryScope = QueryScope.User;
      o.EnableTagProcessing = false;
      o.ShowBanner = false;
    }));

    var options = provider.GetRequiredService<WhizbangCoreOptions>();

    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.Organization);
    await Assert.That(options.EnableTagProcessing).IsFalse();
    await Assert.That(options.ShowBanner).IsFalse();
  }

  [Test]
  public async Task WhizbangCoreOptions_ShowBannerHasOneKey_NotASecondUnderCoreAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Core:ShowBanner"] = "false",
    }, services => services.AddWhizbang());

    await Assert.That(provider.GetRequiredService<WhizbangCoreOptions>().ShowBanner).IsTrue()
      .Because("the banner key is Whizbang:ShowBanner; a second spelling is the duplication #1014 removes");
  }

  [Test]
  [Arguments("true", false, true)]
  [Arguments("false", true, false)]
  [Arguments("not-a-boolean", false, false)]
  public async Task WhizbangCoreOptions_ShowBanner_ConfigurationOverridesCodeWhenReadableAsync(string configured, bool code, bool expected) {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:ShowBanner"] = configured,
    }, services => services.AddWhizbang(o => o.ShowBanner = code));

    await Assert.That(provider.GetRequiredService<WhizbangCoreOptions>().ShowBanner).IsEqualTo(expected);
  }

  [Test]
  public async Task WhizbangCoreOptions_ACodeConfigureAfterAddWhizbangWinsOnTheOptionsViewAsync() {
    await using var provider = _withConfiguration(new() {
      ["Whizbang:Core:DefaultQueryScope"] = "Organization",
    }, services => {
      services.AddWhizbang();
      services.Configure<WhizbangCoreOptions>(o => o.DefaultQueryScope = QueryScope.Global);
    });

    await Assert.That(provider.GetRequiredService<IOptions<WhizbangCoreOptions>>().Value.DefaultQueryScope)
      .IsEqualTo(QueryScope.Global);
  }

  [Test]
  public async Task NoConfigurationRegistered_EveryClassResolvesItsDefaultsAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbang();
    services.AddWhizbangMessageSecurity();
    services.AddWhizbangManagedHealth();
    services.AddSystemEvents();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<RedeliveryPumpOptions>>().Value.MaxEventsPerRequest).IsEqualTo(10_000);
    await Assert.That(provider.GetRequiredService<IOptions<ThrottleRetryOptions>>().Value.MaxAttempts).IsEqualTo(5);
    await Assert.That(provider.GetRequiredService<IOptions<StreamRateLimiterOptions>>().Value.MaxEventsPerWindow).IsEqualTo(50);
    await Assert.That(provider.GetRequiredService<IOptions<DebuggerAwareClockOptions>>().Value.Mode).IsEqualTo(DebuggerDetectionMode.Auto);
    await Assert.That(provider.GetRequiredService<StandbyWatcherOptions>().PollInterval).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(provider.GetRequiredService<IOptions<PerspectiveSnapshotOptions>>().Value.SnapshotEveryNEvents).IsEqualTo(100);
    await Assert.That(provider.GetRequiredService<IOptions<PerspectiveRewindOptions>>().Value.MaxConcurrentRewinds).IsEqualTo(3);
    await Assert.That(provider.GetRequiredService<IOptions<PerspectiveStreamLockOptions>>().Value.LockTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(provider.GetRequiredService<IOptions<PerspectiveStreamAffinityOptions>>().Value.SweepInterval).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(provider.GetRequiredService<IOptions<SystemEventOptions>>().Value.AuditShipSlideSeconds).IsEqualTo(15);
    await Assert.That(provider.GetRequiredService<MessageSecurityOptions>().Timeout).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(provider.GetRequiredService<WhizbangHealthOptions>().SourceTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(provider.GetRequiredService<WhizbangLifecycleOptions>().TransitionAckTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(provider.GetRequiredService<WhizbangCoreOptions>().ShowBanner).IsTrue();
    await Assert.That(provider.GetRequiredService<IOptions<WhizbangCoreOptions>>().Value.DefaultQueryScope).IsEqualTo(QueryScope.Tenant);
  }
}
