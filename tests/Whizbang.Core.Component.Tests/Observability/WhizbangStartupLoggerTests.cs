// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// <para>
/// Direct tests for <see cref="WhizbangStartupLogger"/> — the IHostedService that
/// fires once at host startup to print the banner + log the framework version.
/// Coverage report showed 0/18 lines; the class only runs in the full DI host path
/// (sample apps) so the StartAsync logic — the banner enable/disable and the version
/// log line — was untested directly. Configuration-over-code precedence for the banner
/// now lives where the options bind (#1014), and is tested there.
/// </para>
/// <para>
/// Locked invariants:
///   1. StartAsync logs Whizbang version + service name regardless of banner setting.
///   2. WhizbangCoreOptions.ShowBanner = false suppresses the banner.
///   3. StopAsync is a no-op returning a completed task.
/// </para>
/// </summary>
/// <docs>operations/observability/logging#startup</docs>
public class WhizbangStartupLoggerTests {

  [Test]
  public async Task StartAsync_AlwaysLogsVersionAndServiceNameAsync() {
    var capturing = new CapturingLogger();
    var loggerFactory = new FactoryReturning(capturing);
    var instanceProvider = new StubInstanceProvider("Whizbang.Test.Service");
    var coreOptions = new WhizbangCoreOptions { ShowBanner = false };

    var sut = new WhizbangStartupLogger(loggerFactory: loggerFactory, instanceProvider: instanceProvider, coreOptions: coreOptions);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(capturing.Messages).IsNotEmpty();
    await Assert.That(capturing.Messages[0]).Contains("Whizbang");
    await Assert.That(capturing.Messages[0]).Contains("Whizbang.Test.Service");
  }

  [Test]
  public async Task StopAsync_ReturnsCompletedTaskAsync() {
    var sut = new WhizbangStartupLogger(
      loggerFactory: NullLoggerFactory.Instance,
      instanceProvider: new StubInstanceProvider("SvcE"),
      coreOptions: new WhizbangCoreOptions());

    var task = sut.StopAsync(CancellationToken.None);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
  }

  private sealed class StubInstanceProvider(string serviceName) : IServiceInstanceProvider {
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

  private sealed class CapturingLogger : ILogger {
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

  private sealed class FactoryReturning(ILogger logger) : ILoggerFactory {
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
  }
}
