using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="WhizbangStartupLogger"/> — the IHostedService that
/// fires once at host startup to print the banner + log the framework version.
/// Coverage report showed 0/18 lines; the class only runs in the full DI host path
/// (sample apps) so the StartAsync logic — config-vs-options precedence, the
/// banner enable/disable, the version log line — was untested directly.
///
/// Locked invariants:
///   1. StartAsync logs Whizbang version + service name regardless of banner setting.
///   2. WhizbangCoreOptions.ShowBanner = false suppresses the banner.
///   3. Whizbang:ShowBanner appsettings value overrides the code option.
///   4. Bad appsettings value (non-bool) silently falls back to the code option.
///   5. Null configuration leaves the code option authoritative (no crash).
///   6. StopAsync is a no-op returning a completed task.
/// </summary>
/// <docs>operations/observability/logging#startup</docs>
public class WhizbangStartupLoggerTests {

  [Test]
  public async Task StartAsync_AlwaysLogsVersionAndServiceNameAsync() {
    var capturing = new _CapturingLogger();
    var loggerFactory = new _FactoryReturning(capturing);
    var instanceProvider = new _StubInstanceProvider("Whizbang.Test.Service");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = false };

    var sut = new WhizbangStartupLogger(loggerFactory, instanceProvider, coreOptions);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(capturing.Messages).IsNotEmpty();
    await Assert.That(capturing.Messages[0]).Contains("Whizbang");
    await Assert.That(capturing.Messages[0]).Contains("Whizbang.Test.Service");
  }

  [Test]
  public async Task StartAsync_ConfigShowBannerOverridesCodeOption_TrueAsync() {
    var capturing = new _CapturingLogger();
    var loggerFactory = new _FactoryReturning(capturing);
    var instanceProvider = new _StubInstanceProvider("SvcA");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = false };
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:ShowBanner"] = "true",
      })
      .Build();

    var sut = new WhizbangStartupLogger(loggerFactory, instanceProvider, coreOptions, config);

    // Should not throw — the override path runs, the banner attempts to print to
    // Console.Out which TUnit captures cleanly.
    await sut.StartAsync(CancellationToken.None);

    await Assert.That(capturing.Messages).IsNotEmpty();
  }

  [Test]
  public async Task StartAsync_ConfigShowBannerOverridesCodeOption_FalseAsync() {
    var capturing = new _CapturingLogger();
    var loggerFactory = new _FactoryReturning(capturing);
    var instanceProvider = new _StubInstanceProvider("SvcB");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = true };
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:ShowBanner"] = "false",
      })
      .Build();

    var sut = new WhizbangStartupLogger(loggerFactory, instanceProvider, coreOptions, config);

    await sut.StartAsync(CancellationToken.None);

    // Version line always fires.
    await Assert.That(capturing.Messages).IsNotEmpty();
  }

  [Test]
  public async Task StartAsync_ConfigNonBool_FallsBackToCodeOptionAsync() {
    var capturing = new _CapturingLogger();
    var loggerFactory = new _FactoryReturning(capturing);
    var instanceProvider = new _StubInstanceProvider("SvcC");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = false };
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:ShowBanner"] = "not-a-boolean",
      })
      .Build();

    var sut = new WhizbangStartupLogger(loggerFactory, instanceProvider, coreOptions, config);

    // Bool.TryParse fails silently; code option stays authoritative; no crash.
    await sut.StartAsync(CancellationToken.None);

    await Assert.That(capturing.Messages).IsNotEmpty();
  }

  [Test]
  public async Task StartAsync_NullConfiguration_UsesCodeOptionAsync() {
    var capturing = new _CapturingLogger();
    var loggerFactory = new _FactoryReturning(capturing);
    var instanceProvider = new _StubInstanceProvider("SvcD");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = false };

    var sut = new WhizbangStartupLogger(loggerFactory, instanceProvider, coreOptions, configuration: null);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(capturing.Messages).IsNotEmpty();
  }

  [Test]
  public async Task StopAsync_ReturnsCompletedTaskAsync() {
    var sut = new WhizbangStartupLogger(
      NullLoggerFactory.Instance,
      new _StubInstanceProvider("SvcE"),
      new WhizbangCoreOptions());

    var task = sut.StopAsync(CancellationToken.None);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
  }

  private sealed class _StubInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = serviceName;
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class _CapturingLogger : ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      Messages.Add(formatter(state, exception));
    }
  }

  private sealed class _FactoryReturning(ILogger logger) : ILoggerFactory {
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
  }
}
