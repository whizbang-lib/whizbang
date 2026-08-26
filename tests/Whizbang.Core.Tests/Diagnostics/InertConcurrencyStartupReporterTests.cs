using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// The inert-concurrency report has to actually reach an operator.
/// </summary>
/// <remarks>
/// A diagnostic nobody invokes is documentation. These tests cover the two things that make it
/// real: it runs at startup and emits at a level that is on by default, and it is registered
/// automatically so no consumer has to know it exists.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Diagnostics/InertConcurrencyStartupReporter.cs</code-under-test>
[Category("Diagnostics")]
public class InertConcurrencyStartupReporterTests {

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Noop.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
      => Entries.Add((level, fmt(state, ex)));
    private sealed class Noop : IDisposable { public static readonly Noop Instance = new(); public void Dispose() { } }
  }

  [Test]
  public async Task WarnsAtStartupWhenAConfiguredWidthCannotTakeEffectAsync() {
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    var reporter = new InertConcurrencyStartupReporter(
      logger,
      services: null,
      coordinator: Options.Create(new WorkCoordinatorOptions { ParallelizeStreams = false }),
      orderedStream: Options.Create(new OrderedStreamProcessorOptions { ParallelizeStreams = false }),
      outboxDrain: Options.Create(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 }),
      inboxDispatch: Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 }));

    await reporter.StartAsync(CancellationToken.None);

    var warnings = logger.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(2)
      .Because("both stages are serialized by separate flags, and reporting only one is how an "
             + "operator fixes half the problem and stops looking");
    await Assert.That(warnings.Any(w => w.Message.Contains("128", StringComparison.Ordinal))).IsTrue();
    await Assert.That(warnings.Any(w => w.Message.Contains("64", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task StaysSilentOnACoherentConfigurationAsync() {
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    var reporter = new InertConcurrencyStartupReporter(
      logger,
      services: null,
      coordinator: Options.Create(new WorkCoordinatorOptions { ParallelizeStreams = true }),
      orderedStream: Options.Create(new OrderedStreamProcessorOptions { ParallelizeStreams = true }),
      outboxDrain: Options.Create(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 }),
      inboxDispatch: Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 }));

    await reporter.StartAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.Level >= LogLevel.Warning)).IsEqualTo(0);
  }

  [Test]
  public async Task StartupNeverFailsOnAccountOfThisDiagnosticAsync() {
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    var reporter = new InertConcurrencyStartupReporter(logger);   // no options configured at all

    Exception? captured = null;
    try { await reporter.StartAsync(CancellationToken.None); } catch (Exception ex) { captured = ex; }

    await Assert.That(captured).IsNull()
      .Because("a warning about slow configuration must never become an outage — a host that "
             + "boots today has to keep booting after upgrading");
  }

  [Test]
  public async Task IsRegisteredByAddWhizbangSoNobodyHasToKnowItExistsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang();

    var registered = services.Any(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(InertConcurrencyStartupReporter));

    await Assert.That(registered).IsTrue()
      .Because("turn-key means the deployment that most needs this warning — one that never "
             + "configured anything — is exactly the one that gets it without opting in");
  }

  [Test]
  public async Task ReadsTheOptionsTheWORKERSUse_NotADefaultConstructedIOptionsAsync() {
    // The workers resolve GetRequiredService<WorkCoordinatorOptions>() — a plain singleton.
    // A host that registers it that way and never calls Configure<T> leaves IOptions<T> resolvable
    // but DEFAULT-CONSTRUCTED, so a reporter reading IOptions sees ParallelizeStreams = false on a
    // correctly configured system and warns about a problem that does not exist.
    //
    // This was observed in production: both flags set true in the container, both verified in the
    // pod spec, and the warning fired anyway. A diagnostic that cries wolf on a healthy config is
    // worse than none — it is the exact noise this feature was written to avoid.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(new WorkCoordinatorOptions { ParallelizeStreams = true });
    services.AddSingleton(new OrderedStreamProcessorOptions { ParallelizeStreams = true });
    services.AddSingleton(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 });
    services.AddSingleton(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 });
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    services.AddSingleton<ILogger<InertConcurrencyStartupReporter>>(logger);
    services.AddSingleton<InertConcurrencyStartupReporter>();

    var sp = services.BuildServiceProvider();
    await sp.GetRequiredService<InertConcurrencyStartupReporter>().StartAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.Level >= LogLevel.Warning)).IsEqualTo(0)
      .Because("these are the options the workers actually read; warning here reports a defect "
             + "that is not present and trains operators to ignore the one that is");
  }

  [Test]
  public async Task StillWarnsWhenTheSingletonOptionsAreGenuinelyInertAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(new WorkCoordinatorOptions { ParallelizeStreams = false });
    services.AddSingleton(new OrderedStreamProcessorOptions { ParallelizeStreams = false });
    services.AddSingleton(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 });
    services.AddSingleton(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 });
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    services.AddSingleton<ILogger<InertConcurrencyStartupReporter>>(logger);
    services.AddSingleton<InertConcurrencyStartupReporter>();

    var sp = services.BuildServiceProvider();
    await sp.GetRequiredService<InertConcurrencyStartupReporter>().StartAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.Level >= LogLevel.Warning)).IsEqualTo(2)
      .Because("fixing the false positive must not disable the true positive");
  }
}
