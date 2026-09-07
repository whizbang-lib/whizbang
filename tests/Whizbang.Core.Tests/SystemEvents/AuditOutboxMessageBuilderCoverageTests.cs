using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The resolve-failure arm of <c>AuditOutboxMessageBuilder._resolveEventType</c>, which no test
/// reached because nothing in the suite made <see cref="Type.GetType(string)"/> actually THROW as
/// opposed to returning null.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
[Category("SystemEvents")]
public class AuditOutboxMessageBuilderCoverageTests {

  // Type.GetType(name) returns null for a type it cannot FIND, but it THROWS for a name it cannot
  // PARSE -- throwOnError only suppresses TypeLoadException, not the exceptions raised while the
  // assembly segment is being read. A malformed Version= raises FileLoadException out of
  // TypeNameParser before any lookup happens. (Same BCL behavior that produced a real bug in
  // MultiPassMessageTypeBinder this round, where it escaped the binder entirely.)
  //
  // Here the stakes are quieter but the same shape: a stored MessageType that no longer parses --
  // after a rename, an assembly split, or a hand-edited row -- must degrade to "no audit decision
  // available" WITH a log naming the type. If the throw escaped instead, one unparseable row would
  // fault the audit builder for every event behind it in the batch; if it were swallowed silently,
  // audit would quietly stop applying to that event type and nothing would say so.
  [Test]
  public async Task TryBuildAuditMessage_MessageTypeThatCannotBeParsed_LogsAndFallsBackInsteadOfThrowingAsync() {
    var logger = new CapturingLogger();
    var options = new SystemEventOptions().EnableEventAudit();
    var source = _outboxEventWithMessageType(
      "Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core, Version=not-a-version");

    var built = _record(() => AuditOutboxMessageBuilder.TryBuildAuditMessage(source, options, logger));

    await Assert.That(built.Error).IsNull()
      .Because("an unparseable stored type name must not fault the audit builder -- every later "
             + "message in the same batch would go unaudited because of one bad row");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error))
      .IsTrue()
      .Because("silently dropping the audit decision would make a renamed or removed event type "
             + "look exactly like one that was deliberately configured not to be audited");
    await Assert.That(logger.Entries.Any(e => e.Message is not null
        && e.Message.Contains("not-a-version", StringComparison.Ordinal))).IsTrue()
      .Because("the log has to name the offending type, or an operator has a warning and no way "
             + "to find which stored row produced it");
  }

  private static (OutboxMessage? Value, Exception? Error) _record(Func<OutboxMessage?> build) {
    try {
      return (build(), null);
    } catch (Exception ex) {
      return (null, ex);
    }
  }

  private static OutboxMessage _outboxEventWithMessageType(string messageType) {
    var hop = new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
    };
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [hop] },
      EnvelopeType = "T",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = messageType,
      Scope = null,
    };
  }

  /// <summary>Captures level and formatted message so the diagnostic itself can be asserted.</summary>
  private sealed class CapturingLogger : ILogger {
    private readonly List<(LogLevel Level, string? Message)> _entries = [];
    public List<(LogLevel Level, string? Message)> Entries { get { lock (_entries) { return [.. _entries]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
    }
  }
}
