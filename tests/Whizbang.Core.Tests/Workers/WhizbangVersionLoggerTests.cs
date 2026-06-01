using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the startup-time Whizbang version log so operators can verify which build is
/// actually running in a deployed pod via `kubectl logs | grep "Whizbang loaded"`.
/// </summary>
public class WhizbangVersionLoggerTests {

  [Test]
  public async Task StartAsync_LogsAssemblyNameAndVersionAsync() {
    var captured = new _CapturingLogger();
    var logger = new _SingleScopeLogger<WhizbangVersionLogger>(captured);
    var sut = new WhizbangVersionLogger(logger);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(captured.Entries.Count)
      .IsEqualTo(1)
      .Because("startup logger must log exactly once");
    var entry = captured.Entries[0];
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(entry.Message).Contains("Whizbang loaded");
    await Assert.That(entry.Message).Contains("Whizbang.Core");
  }

  [Test]
  public async Task StopAsync_NoOpAsync() {
    var captured = new _CapturingLogger();
    var logger = new _SingleScopeLogger<WhizbangVersionLogger>(captured);
    var sut = new WhizbangVersionLogger(logger);

    await sut.StartAsync(CancellationToken.None);
    captured.Entries.Clear();

    await sut.StopAsync(CancellationToken.None);

    await Assert.That(captured.Entries.Count)
      .IsEqualTo(0)
      .Because("StopAsync must not emit log lines");
  }

  private sealed class _CapturingEntry {
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }
  }

  private sealed class _CapturingLogger {
    public List<_CapturingEntry> Entries { get; } = [];
  }

  private sealed class _SingleScopeLogger<T>(_CapturingLogger sink) : ILogger<T> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NoopScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      sink.Entries.Add(new _CapturingEntry { Level = logLevel, Message = formatter(state, exception) });
    }
  }

  private sealed class _NoopScope : IDisposable {
    public static readonly _NoopScope Instance = new();
    public void Dispose() { }
  }
}
