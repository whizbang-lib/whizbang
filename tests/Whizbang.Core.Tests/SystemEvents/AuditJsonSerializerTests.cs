using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for <see cref="AuditJsonSerializer"/>. When a value cannot be serialized (no JsonTypeInfo in
/// any registered context), it persists an empty <c>{}</c> audit payload — which must be logged rather
/// than silently written, so the compliance gap is diagnosable (swallow-audit finding).
/// </summary>
public class AuditJsonSerializerTests {
  [Test]
  public async Task SerializeToJsonElement_UnregisteredType_LogsWarningAndReturnsEmptyObjectAsync() {
    var captured = new _capturingLogger();
    var options = JsonContextRegistry.CreateCombinedOptions();

    var result = AuditJsonSerializer.SerializeToJsonElement(new _unregisteredAuditPayload("x"), options, captured);

    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(result.EnumerateObject().Any()).IsFalse()
      .Because("an unserializable audit payload is persisted as an empty {} object");
    await Assert.That(captured.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("writing an empty audit payload is a compliance gap that must be logged, not silent");
  }

  private sealed record _unregisteredAuditPayload(string Value);

  private sealed class _capturingLogger : ILogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception), exception));
  }
}
