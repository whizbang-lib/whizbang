// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Issue #915: a composite's inner list is polymorphic <see cref="IMessage"/>, and a consumer that
/// handles only SOME of the inner event types has no metadata for the rest. Before the fix, one inner
/// element whose discriminator the consumer could not resolve failed the WHOLE composite
/// ("The JSON payload for polymorphic interface or abstract type 'IMessage' must specify a type
/// discriminator"), so the consumer lost the inner events it does handle too. An unresolvable inner
/// element now reads as <see cref="UnresolvedMessage"/>, and fan-out drops it the way it drops a child
/// this consumer does not subscribe to: a type the consumer cannot even name cannot have a consumer here.
/// </summary>
[Category("Messaging")]
[Category("JsonSerialization")]
public class CompositeUnresolvedInnerMessageTests {

  /// <summary>A consumer's composite. Public so the source generator registers it like a real one.</summary>
  public sealed class UnresolvedInnerProbeComposite : CompositeEventBase;

  /// <summary>An inner event this consumer handles. Public so it is registered as an IMessage derived type.</summary>
  public sealed record HandledInnerEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Note { get; init; } = string.Empty;
  }

  /// <summary>A second inner event; its discriminator is rewritten to one no context knows.</summary>
  public sealed record OtherInnerEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Note { get; init; } = string.Empty;
  }

  private const string UNKNOWN_DISCRIMINATOR = "Elsewhere.Contracts.EventThisServiceNeverReferenced";

  [Test]
  public async Task Deserialize_InnerDiscriminatorUnresolvable_KeepsResolvableInnerEventsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = _compositeJsonWithOneUnresolvableInner(options, out var streamId);

    var composite = (UnresolvedInnerProbeComposite?)JsonSerializer.Deserialize(
      json, options.GetTypeInfo(typeof(UnresolvedInnerProbeComposite)));

    await Assert.That(composite).IsNotNull();
    await Assert.That(composite!.StreamId).IsEqualTo(streamId);
    await Assert.That(composite.Inner.Count).IsEqualTo(2)
      .Because("an inner element this consumer cannot resolve must not fail the whole composite");
    await Assert.That(composite.Inner[0]).IsTypeOf<HandledInnerEvent>();
    await Assert.That(((HandledInnerEvent)composite.Inner[0]).Note).IsEqualTo("handled");
    await Assert.That(composite.Inner[1]).IsTypeOf<UnresolvedMessage>();
  }

  [Test]
  public async Task Serialize_UnresolvedPlaceholder_RoundTripsAsPlaceholderAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(UnresolvedInnerProbeComposite));
    var original = new UnresolvedInnerProbeComposite {
      StreamId = Guid.CreateVersion7(),
      Inner = [new HandledInnerEvent { Note = "kept" }, new UnresolvedMessage()],
    };

    var json = JsonSerializer.Serialize(original, typeInfo);
    var roundTripped = (UnresolvedInnerProbeComposite?)JsonSerializer.Deserialize(json, typeInfo);

    await Assert.That(roundTripped!.Inner.Count).IsEqualTo(2)
      .Because("a consumer stores the composite as an inbox row before fan-out, so a placeholder it read "
             + "must survive being written and read back");
    await Assert.That(roundTripped.Inner[0]).IsTypeOf<HandledInnerEvent>();
    await Assert.That(roundTripped.Inner[1]).IsTypeOf<UnresolvedMessage>();
  }

  [Test]
  public async Task Deserialize_TopLevelMessageWithUnknownDiscriminator_StillRefusedAsync() {
    // The tolerance is for an ELEMENT nested inside a message the consumer does resolve. A top-level
    // polymorphic payload names the message itself; an unknown one there must still fail loudly.
    var options = JsonContextRegistry.CreateCombinedOptions();
    var envelopeInfo = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<IMessage>(options)!;
    // This envelope typeinfo is hand-built with the long member names (MessageId / Payload / Hops).
    var json = "{\"MessageId\":\"" + Guid.CreateVersion7() + "\",\"Payload\":{\"$type\":\"" + UNKNOWN_DISCRIMINATOR + "\"},\"Hops\":[]}";

    await Assert.That(() => JsonSerializer.Deserialize(json, envelopeInfo))
      .Throws<NotSupportedException>()
      .WithMessageContaining("type discriminator");
  }

  [Test]
  public async Task TryExpand_AtomicCompositeWithUnresolvedInner_ExpandsResolvableChildrenAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var streamId = Guid.CreateVersion7();
    var composite = new UnresolvedInnerProbeComposite {
      StreamId = streamId,
      Atomicity = FanoutAtomicity.Atomic,
      Inner = [new HandledInnerEvent { StreamId = streamId, Note = "kept" }, new UnresolvedMessage()],
    };
    var logs = new List<(LogLevel Level, string Message)>();
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(options))
      .AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)))
      .BuildServiceProvider();

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(streamId), sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded)
      .Because("an inner type this consumer cannot resolve is one it cannot consume; under Atomic that is "
             + "an unsubscribed child, not a failed one, or every mixed composite would dead-letter");
    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(result.Children[0].MessageType).Contains(nameof(HandledInnerEvent));
    await Assert.That(result.UnsubscribedChildren).IsEqualTo(1);
    await Assert.That(logs.Any(l => l.Level >= LogLevel.Information && l.Message.Contains("could not resolve", StringComparison.Ordinal)))
      .IsTrue()
      .Because("skipping an inner event is never silent: the count is logged per composite");
  }

  // ------------------------------------------------------------------------------------------

  private static string _compositeJsonWithOneUnresolvableInner(JsonSerializerOptions options, out Guid streamId) {
    streamId = Guid.CreateVersion7();
    var composite = new UnresolvedInnerProbeComposite {
      StreamId = streamId,
      Inner = [new HandledInnerEvent { Note = "handled" }, new OtherInnerEvent { Note = "unknown here" }],
    };
    var json = JsonSerializer.Serialize(composite, options.GetTypeInfo(typeof(UnresolvedInnerProbeComposite)));
    var otherDiscriminator = typeof(OtherInnerEvent).FullName!.Replace('+', '.');
    if (!json.Contains(otherDiscriminator, StringComparison.Ordinal)) {
      throw new InvalidOperationException($"Fixture assumption broken: discriminator '{otherDiscriminator}' not in {json}");
    }
    return json.Replace(otherDiscriminator, UNKNOWN_DISCRIMINATOR, StringComparison.Ordinal);
  }

  private static MessageEnvelope<JsonElement> _sourceEnvelope(Guid streamId) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = JsonSerializer.SerializeToElement(new { }),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = new Dictionary<string, JsonElement> {
        ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
      },
    }],
  };

  private sealed class ListLoggerProvider(List<(LogLevel Level, string Message)> entries) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new Sink(entries);
    public void Dispose() { }

    private sealed class Sink(List<(LogLevel Level, string Message)> entries) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (entries) {
          entries.Add((logLevel, formatter(state, exception)));
        }
      }
    }
  }
}
