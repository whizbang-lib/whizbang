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

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// What auditing does when it cannot resolve the event type a message names.
/// <para>
/// The audit decision starts from the CLR type, because that is where the opt-in and opt-out
/// attributes live. A message can name a type this process does not have — an event renamed or
/// removed since the row was written, or one owned by another service — and when that happens the
/// builder falls back to recording rather than dropping. Auditing is a compliance surface, so
/// erring toward a record that might not have been required is the safe direction; erring toward
/// silence loses history that cannot be reconstructed.
/// </para>
/// <para>
/// The fallback is logged for the same reason. A silent fallback makes a renamed event type look
/// exactly like one that was never audited, and the difference only surfaces when someone goes
/// looking for records that were never written.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
public class AuditEventTypeResolutionTests {

  private sealed class CapturingLogger : ILogger {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public List<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
  }

  private static OutboxMessage _outboxEvent(string messageType) {
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
    };
  }

  [Test]
  public async Task AnUnresolvableEventType_IsStillAuditedAndSaidSoAsync() {
    // Compliance direction: a record that may not have been required is recoverable, silence is not.
    var logger = new CapturingLogger();
    var source = _outboxEvent("Some.Renamed.Event, Some.Assembly.That.Is.Not.Here");

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(
      source, new SystemEventOptions().EnableEventAudit(), logger);

    await Assert.That(audit).IsNotNull()
      .Because("a type this process cannot resolve must still be recorded — losing history to a "
             + "rename is not recoverable, an unnecessary record is");
    await Assert.That(logger.Messages.Count).IsGreaterThan(0)
      .Because("a silent fallback makes a renamed event look exactly like one never audited, and "
             + "the difference only shows when someone hunts for records that were never written");
  }

  [Test]
  public async Task AMalformedTypeName_FallsBackRatherThanThrowingAsync() {
    // Type.GetType can throw on a malformed assembly-qualified name. That must not take the whole
    // outbox drain down: one unreadable message would stall every message behind it.
    var logger = new CapturingLogger();
    var source = _outboxEvent("!!not a type name!!, [[broken]]");

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(
      source, new SystemEventOptions().EnableEventAudit(), logger);

    await Assert.That(audit).IsNotNull()
      .Because("a malformed name is still a message that happened, and throwing here would stall "
             + "every message behind it in the drain");
  }

  [Test]
  public async Task AResolvableEventType_TakesTheNormalDecisionPathAsync() {
    // The contrast: a type this process does have goes through the attribute-driven decision
    // rather than the fallback, so the fallback is genuinely the exception and not the default.
    var logger = new CapturingLogger();
    var source = _outboxEvent("Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core");

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(
      source, new SystemEventOptions().EnableEventAudit(), logger);

    await Assert.That(audit).IsNotNull();
    await Assert.That(logger.Messages.Any(m => m.Contains("unresolved", StringComparison.OrdinalIgnoreCase))).IsFalse()
      .Because("a resolvable type must not report the fallback, or the log stops distinguishing "
             + "the two cases it exists to separate");
  }
}
