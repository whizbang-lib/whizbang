using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>One binding lock per framework options class (issue #646). The audited failure
/// mode: AddOptions&lt;T&gt;() with no IConfigureOptions&lt;T&gt; resolves default-constructed
/// instances forever, so documented configuration sections silently did nothing — the
/// StreamIntegrity disable flags (#666) were the live casualty. Every class the turnkey
/// pipeline registers gets a concrete binder-generator Bind and a test here proving a
/// non-default value survives the trip from configuration to the resolved instance.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference</docs>
[Category("Shard2")]
public sealed class AllOptionsBindingMatrixTests {

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task EphemeralOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Ephemeral:ReconcileHistoricalOnStartup"] = "true",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Configuration.EphemeralOptions>>().Value;
    await Assert.That(options.ReconcileHistoricalOnStartup).IsTrue()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task PerspectiveRowRetentionOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:PerspectiveRowRetention:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Configuration.PerspectiveRowRetentionOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task SchemaInitializationOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:SchemaInitialization:NonBlockingSchemaInit"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.SchemaInitializationOptions>>().Value;
    await Assert.That(options.NonBlockingSchemaInit).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task UnobservedExceptionDiagnosticsOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:UnobservedExceptionDiagnostics:EnableFirstChanceExceptionLogging"] = "true",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Observability.UnobservedExceptionDiagnosticsOptions>>().Value;
    await Assert.That(options.EnableFirstChanceExceptionLogging).IsTrue()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task BackupTickCoordinatorOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:BackupTick:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.BackupTickCoordinatorOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task BacklogAgeOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:BacklogAge:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Observability.BacklogAgeOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task HeartbeatWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:Heartbeat:IntervalSeconds"] = "77",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.HeartbeatWorkerOptions>>().Value;
    await Assert.That(options.IntervalSeconds).IsEqualTo(77)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task OutboxCompletionFlushWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:OutboxCompletionFlush:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.OutboxCompletionFlushWorkerOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task PerspectiveCompletionFlushWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:PerspectiveCompletionFlush:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.PerspectiveCompletionFlushWorkerOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task FailureFlushWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:FailureFlush:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.FailureFlushWorkerOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task LeaseRenewalWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:LeaseRenewal:LeaseSeconds"] = "555",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.LeaseRenewalWorkerOptions>>().Value;
    await Assert.That(options.LeaseSeconds).IsEqualTo(555)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task InboxHandlerWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:InboxHandler:Enabled"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.InboxHandlerWorkerOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task OutboxPublishWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:OutboxPublish:Enabled"] = "true",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.OutboxPublishWorkerOptions>>().Value;
    await Assert.That(options.Enabled).IsTrue()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task InboxDispatchWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:InboxDispatch:MaxConcurrentDispatch"] = "17",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.InboxDispatchWorkerOptions>>().Value;
    await Assert.That(options.MaxConcurrentDispatch).IsEqualTo(17)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task MaintenanceWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:IntervalMinutes"] = "42",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.MaintenanceWorkerOptions>>().Value;
    await Assert.That(options.IntervalMinutes).IsEqualTo(42)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task OutboxDrainWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:OutboxDrain:MaxPerStream"] = "33",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.OutboxDrainWorkerOptions>>().Value;
    await Assert.That(options.MaxPerStream).IsEqualTo(33)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task InboxDrainWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:InboxDrain:MaxPerStream"] = "44",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.InboxDrainWorkerOptions>>().Value;
    await Assert.That(options.MaxPerStream).IsEqualTo(44)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task RecentlyProcessedEventCacheOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:RecentlyProcessedEventCache:TtlMinutes"] = "11",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.RecentlyProcessedEventCacheOptions>>().Value;
    await Assert.That(options.TtlMinutes).IsEqualTo(11)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task InboxDeserializeCacheOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:InboxDeserializeCache:TtlMinutes"] = "9",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.InboxDeserializeCacheOptions>>().Value;
    await Assert.That(options.TtlMinutes).IsEqualTo(9)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task LeaseHandleOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:LeaseHandle:LeaseGraceSeconds"] = "61",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.LeaseHandleOptions>>().Value;
    await Assert.That(options.LeaseGraceSeconds).IsEqualTo(61)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task SlidingWindowOutboxOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:OutboxBatch:MaxSize"] = "250",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.SlidingWindowOutboxOptions>>().Value;
    await Assert.That(options.MaxSize).IsEqualTo(250)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task SlidingWindowInboxOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:InboxBatch:MaxSize"] = "1500",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.SlidingWindowInboxOptions>>().Value;
    await Assert.That(options.MaxSize).IsEqualTo(1500)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task WorkCoordinatorOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinator:PartitionCount"] = "2048",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Messaging.WorkCoordinatorOptions>>().Value;
    await Assert.That(options.PartitionCount).IsEqualTo(2048)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task TemporalOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Temporal:BackstopIntervalMilliseconds"] = "9000",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Temporal.TemporalOptions>>().Value;
    await Assert.That(options.BackstopIntervalMilliseconds).IsEqualTo(9000)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task PerspectiveWorkerOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:Perspective:PerspectiveBatchSize"] = "64",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Workers.PerspectiveWorkerOptions>>().Value;
    await Assert.That(options.PerspectiveBatchSize).IsEqualTo(64)
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task OrderedStreamProcessorOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:OrderedStreamProcessor:ParallelizeStreams"] = "true",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Messaging.OrderedStreamProcessorOptions>>().Value;
    await Assert.That(options.ParallelizeStreams).IsTrue()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task WhizbangOptions_BindsTurnkeyAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:ShowBanner"] = "false",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Configuration.WhizbangOptions>>().Value;
    await Assert.That(options.ShowBanner).IsFalse()
      .Because("#646: an options class the turnkey pipeline registers but never binds is a "
             + "silent lie — the documented section must reach the running instance");
  }

  [Test]
  public async Task WhizbangPinnedPoolOptions_ConfigBindsThenCodeWinsAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:PinnedPool:Size"] = "3",
      ["Whizbang:Workers:PinnedPool:Enabled"] = "true",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangPinnedWorkerPool(o => { });
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<WhizbangPinnedPoolOptions>>().Value;
    await Assert.That(options.Size).IsEqualTo(3)
      .Because("the section the class XML doc has promised all along finally binds turnkey; "
             + "the host's code callback still runs after and can override");
  }

  [Test]
  public async Task SignalBusOptions_BindsTurnkeyAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:SignalBus:ProbeTimeoutMilliseconds"] = "1234",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangSignalBus();
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Signals.SignalBusOptions>>().Value;
    await Assert.That(options.ProbeTimeoutMilliseconds).IsEqualTo(1234);
  }

}
