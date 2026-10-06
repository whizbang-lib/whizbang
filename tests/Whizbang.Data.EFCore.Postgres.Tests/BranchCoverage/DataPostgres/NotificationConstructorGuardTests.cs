// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The constructor contracts of the PostgreSQL notification stack. Every one of these types is
/// built by the container, and a missing registration must fail at construction, naming the
/// dependency that was missing, rather than surfacing later as a <see cref="NullReferenceException"/>
/// on a background thread with no indication of which service was absent. The optional
/// dependencies are the other half: leaving them out must construct a working instance.
/// </summary>
/// <remarks>No database: every case fails or completes before a connection is resolved.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalTailWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleClaimer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleManager.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleOccurrenceStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresSignalTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSourceBase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Shard5")]
public class NotificationConstructorGuardTests {
  private static readonly IOptions<WhizbangNotificationOptions> _options = Options.Create(new WhizbangNotificationOptions());
  private static readonly IConfiguration _configuration = new ConfigurationBuilder().Build();
  private static readonly IServiceInstanceProvider _instance =
    new ServiceInstanceProvider(Guid.NewGuid(), "guard-svc", "guard-host", processId: 1);
  private static readonly IOptions<ClaimWorkerOptions> _claimOptions = Options.Create(new ClaimWorkerOptions());
  private static readonly IOptions<TemporalOptions> _temporalOptions = Options.Create(new TemporalOptions());
  private static readonly IOptions<CommitOrderStamperOptions> _stamperOptions = Options.Create(new CommitOrderStamperOptions());

  // ---- PgAppSignalChannel ----

  [Test]
  public async Task PgAppSignalChannel_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgAppSignalChannel(null!, _configuration, new NoopSharedConnection(), NullLogger<PgAppSignalChannel>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgAppSignalChannel_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgAppSignalChannel(_options, null!, new NoopSharedConnection(), NullLogger<PgAppSignalChannel>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgAppSignalChannel_NullSharedConnection_NamesSharedConnectionAsync() {
    var ex = await Assert.That(() => new PgAppSignalChannel(_options, _configuration, null!, NullLogger<PgAppSignalChannel>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("sharedConnection");
  }

  [Test]
  public async Task PgAppSignalChannel_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgAppSignalChannel(_options, _configuration, new NoopSharedConnection(), null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgCommitOrderStamperWorker ----

  [Test]
  public async Task PgCommitOrderStamperWorker_NullNotificationOptions_NamesThemAsync() {
    var ex = await Assert.That(() => new PgCommitOrderStamperWorker(
        null!, _stamperOptions, _configuration, new NoopSharedConnection(), NullLogger<PgCommitOrderStamperWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("notificationOptions");
  }

  [Test]
  public async Task PgCommitOrderStamperWorker_NullStamperOptions_NamesThemAsync() {
    var ex = await Assert.That(() => new PgCommitOrderStamperWorker(
        _options, null!, _configuration, new NoopSharedConnection(), NullLogger<PgCommitOrderStamperWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("stamperOptions");
  }

  [Test]
  public async Task PgCommitOrderStamperWorker_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgCommitOrderStamperWorker(
        _options, _stamperOptions, null!, new NoopSharedConnection(), NullLogger<PgCommitOrderStamperWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgCommitOrderStamperWorker_NullSharedConnection_NamesSharedConnectionAsync() {
    var ex = await Assert.That(() => new PgCommitOrderStamperWorker(
        _options, _stamperOptions, _configuration, null!, NullLogger<PgCommitOrderStamperWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("sharedConnection");
  }

  [Test]
  public async Task PgCommitOrderStamperWorker_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgCommitOrderStamperWorker(
        _options, _stamperOptions, _configuration, new NoopSharedConnection(), null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgDurableSignalRetentionWorker ----

  [Test]
  public async Task PgDurableSignalRetentionWorker_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgDurableSignalRetentionWorker(null!, _configuration, NullLogger<PgDurableSignalRetentionWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgDurableSignalRetentionWorker_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgDurableSignalRetentionWorker(_options, null!, NullLogger<PgDurableSignalRetentionWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgDurableSignalRetentionWorker_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgDurableSignalRetentionWorker(_options, _configuration, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgDurableSignalTailWorker ----

  [Test]
  public async Task PgDurableSignalTailWorker_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgDurableSignalTailWorker(
        null!, _configuration, _instance, new NoopSink(), NullLogger<PgDurableSignalTailWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgDurableSignalTailWorker_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgDurableSignalTailWorker(
        _options, null!, _instance, new NoopSink(), NullLogger<PgDurableSignalTailWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgDurableSignalTailWorker_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PgDurableSignalTailWorker(
        _options, _configuration, null!, new NoopSink(), NullLogger<PgDurableSignalTailWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  [Test]
  public async Task PgDurableSignalTailWorker_NullSink_NamesSinkAsync() {
    var ex = await Assert.That(() => new PgDurableSignalTailWorker(
        _options, _configuration, _instance, null!, NullLogger<PgDurableSignalTailWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("sink");
  }

  [Test]
  public async Task PgDurableSignalTailWorker_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgDurableSignalTailWorker(
        _options, _configuration, _instance, new NoopSink(), null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgDutyElector ----

  [Test]
  public async Task PgDutyElector_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgDutyElector(null!, _configuration, _instance, NullLogger<PgDutyElector>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgDutyElector_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgDutyElector(_options, null!, _instance, NullLogger<PgDutyElector>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgDutyElector_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PgDutyElector(_options, _configuration, null!, NullLogger<PgDutyElector>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  [Test]
  public async Task PgDutyElector_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgDutyElector(_options, _configuration, _instance, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgInstanceLifecycleMonitor ----

  [Test]
  public async Task PgInstanceLifecycleMonitor_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgInstanceLifecycleMonitor(null!, _configuration, NullSignalBus.Instance, NullLogger<PgInstanceLifecycleMonitor>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgInstanceLifecycleMonitor_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgInstanceLifecycleMonitor(_options, null!, NullSignalBus.Instance, NullLogger<PgInstanceLifecycleMonitor>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgInstanceLifecycleMonitor_NullSignalBus_NamesSignalBusAsync() {
    var ex = await Assert.That(() => new PgInstanceLifecycleMonitor(_options, _configuration, null!, NullLogger<PgInstanceLifecycleMonitor>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("signalBus");
  }

  [Test]
  public async Task PgInstanceLifecycleMonitor_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgInstanceLifecycleMonitor(_options, _configuration, NullSignalBus.Instance, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgScheduleClaimer ----

  [Test]
  public async Task PgScheduleClaimer_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgScheduleClaimer(
        null!, _configuration, _instance, _claimOptions, _temporalOptions, NullLogger<PgScheduleClaimer>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgScheduleClaimer_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgScheduleClaimer(
        _options, null!, _instance, _claimOptions, _temporalOptions, NullLogger<PgScheduleClaimer>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgScheduleClaimer_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PgScheduleClaimer(
        _options, _configuration, null!, _claimOptions, _temporalOptions, NullLogger<PgScheduleClaimer>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  [Test]
  public async Task PgScheduleClaimer_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgScheduleClaimer(
        _options, _configuration, _instance, _claimOptions, _temporalOptions, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgScheduleManager ----

  [Test]
  public async Task PgScheduleManager_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgScheduleManager(
        null!, _configuration, _instance, _claimOptions, _temporalOptions, NullLogger<PgScheduleManager>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgScheduleManager_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgScheduleManager(
        _options, null!, _instance, _claimOptions, _temporalOptions, NullLogger<PgScheduleManager>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgScheduleManager_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PgScheduleManager(
        _options, _configuration, _instance, _claimOptions, _temporalOptions, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  [Test]
  public async Task PgScheduleManager_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PgScheduleManager(
        _options, _configuration, null!, _claimOptions, _temporalOptions, NullLogger<PgScheduleManager>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  // ---- PgScheduleOccurrenceStore ----

  [Test]
  public async Task PgScheduleOccurrenceStore_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgScheduleOccurrenceStore(null!, _configuration))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgScheduleOccurrenceStore_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgScheduleOccurrenceStore(_options, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  // ---- PgSharedNotifyConnection ----

  [Test]
  public async Task PgSharedNotifyConnection_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgSharedNotifyConnection(null!, _configuration, _instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PgSharedNotifyConnection_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PgSharedNotifyConnection(_options, null!, _instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PgSharedNotifyConnection_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PgSharedNotifyConnection(_options, _configuration, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  // The logger is optional for this one type: it is constructed by the turnkey wiring before logging
  // is guaranteed to exist. Leaving it out must still give a working gate whose failure path (the one
  // that logs) runs without throwing.
  [Test]
  public async Task PgSharedNotifyConnection_WithoutALogger_StillReportsAnUnresolvableConnectionAsync() {
    var gate = new PgSharedNotifyConnection(_options, _configuration, _instance, logger: null);

    var ok = await gate.ProbeNowAsync(CancellationToken.None);

    await Assert.That(ok).IsFalse();
    await Assert.That(gate.IsAvailable).IsFalse();
    await Assert.That(gate.LastFailureReason).IsEqualTo("no connection string resolvable");
  }

  // ---- PostgresSignalTransport ----

  [Test]
  public async Task PostgresSignalTransport_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PostgresSignalTransport(
        null!, _configuration, new NoopSharedConnection(), _instance, NullLogger<PostgresSignalTransport>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task PostgresSignalTransport_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new PostgresSignalTransport(
        _options, null!, new NoopSharedConnection(), _instance, NullLogger<PostgresSignalTransport>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task PostgresSignalTransport_NullSharedConnection_NamesSharedConnectionAsync() {
    var ex = await Assert.That(() => new PostgresSignalTransport(
        _options, _configuration, null!, _instance, NullLogger<PostgresSignalTransport>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("sharedConnection");
  }

  [Test]
  public async Task PostgresSignalTransport_NullInstanceProvider_NamesInstanceProviderAsync() {
    var ex = await Assert.That(() => new PostgresSignalTransport(
        _options, _configuration, new NoopSharedConnection(), null!, NullLogger<PostgresSignalTransport>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("instanceProvider");
  }

  [Test]
  public async Task PostgresSignalTransport_NullLogger_NamesLoggerAsync() {
    var ex = await Assert.That(() => new PostgresSignalTransport(
        _options, _configuration, new NoopSharedConnection(), _instance, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---- PgWorkAvailablePollSourceBase (through a concrete source) ----

  [Test]
  public async Task WorkAvailablePollSource_NullOptions_NamesOptionsAsync() {
    var ex = await Assert.That(() => new PgOutboxWorkAvailablePollSource(
        new FakeTimeProvider(), null!, _configuration, _instance, NullLogger<PgOutboxWorkAvailablePollSource>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  // ---- configuration binder ----

  [Test]
  public async Task ConfigureNotificationOptionsFromConfiguration_NullConfiguration_NamesConfigurationAsync() {
    var ex = await Assert.That(() => new ConfigureWhizbangNotificationOptionsFromConfiguration(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  private sealed class NoopSharedConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) => new NoopDisposable();
  }

  private sealed class NoopDisposable : IDisposable {
    public void Dispose() {
      // Nothing was subscribed.
    }
  }

  private sealed class NoopSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal => ValueTask.CompletedTask;
  }
}
