// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>A strong id a source command's stream is keyed by (#2072).</summary>
[WhizbangId]
public readonly partial struct InheritanceOrderId;

/// <summary>A strong id a cascaded event's stream is keyed by (#2072).</summary>
[WhizbangId]
public readonly partial struct InheritanceInvoiceId;

/// <summary>
/// The stream a cascaded event inherits from the message that caused it, when the event's <c>[StreamId]</c> is a strong
/// id (#2072). The generated setter refuses a source of another id type, or a value the id's <c>From</c> refuses; the
/// dispatcher then gives the event no inherited stream at all, so the stream it tracks (the envelope's) and the event's
/// own <c>[StreamId]</c> agree. After a refusal an event with <c>[GenerateStreamId]</c> mints its own id, as for a
/// cascade with no source stream, and one without it has no stream id. Each refused pair of types is logged once.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("StreamId")]
[Category("Cascade")]
[NotInParallel("StreamIdExtractorRegistry")] // ScopedEventTrackerAccessor.CurrentTracker is static, and the extractors are a static registry
public sealed class DispatcherStrongIdStreamInheritanceTests {
  public sealed class PlaceInheritanceOrder : ICommand {
    [StreamId] public InheritanceOrderId OrderId { get; set; }
  }

  public sealed class NoteInheritanceOrder : ICommand {
    [StreamId] public Guid OrderId { get; set; }
  }

  public sealed class InheritanceInvoiceRaised : IEvent {
    [StreamId] public InheritanceInvoiceId InvoiceId { get; set; }
  }

  public sealed class InheritanceInvoiceFromOrder : IEvent {
    [StreamId(InheritFrom = [typeof(InheritanceOrderId)])] public InheritanceInvoiceId InvoiceId { get; set; }
  }

  public sealed class InheritanceInvoiceNoted : IEvent {
    [StreamId] public Guid InvoiceId { get; init; }
  }

  [GenerateStreamId]
  public sealed class InheritanceInvoiceMinted : IEvent {
    [StreamId] public InheritanceInvoiceId InvoiceId { get; set; }
  }

  /// <summary>A version 7 Guid, which every strong id's <c>From</c> takes.</summary>
  private static readonly Guid _v7 = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057");

  /// <summary>A version 4 Guid, which a strong id that takes only UUIDv7 refuses.</summary>
  private static readonly Guid _v4 = Guid.Parse("6f9619ff-8b86-4011-b42d-00c04fc964ff");

  private static MessageEnvelope<object> _sourceEnvelope(object payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static PlaceInheritanceOrder _orderSource() => new() { OrderId = InheritanceOrderId.From(_v7) };

  [Test]
  public async Task ASourceOfAnotherStrongId_LeavesTheEnvelopeAndTheMessageWithoutAStreamAsync() {
    var cascaded = new InheritanceInvoiceRaised();

    var tracked = await _cascadeAsync(cascaded, _orderSource());

    await Assert.That(tracked).IsEqualTo(Guid.Empty)
      .Because("the envelope must not carry a stream the message refused, or the two would disagree");
    await Assert.That(cascaded.InvoiceId.ToGuid()).IsEqualTo(Guid.Empty)
      .Because("an OrderId stream is not an InvoiceId stream unless the event says it may be");
  }

  [Test]
  public async Task AGuidSourceTheStrongIdsFromRefuses_LeavesTheEnvelopeAndTheMessageWithoutAStreamAsync() {
    var cascaded = new InheritanceInvoiceRaised();

    var tracked = await _cascadeAsync(cascaded, new NoteInheritanceOrder { OrderId = _v4 });

    await Assert.That(tracked).IsEqualTo(Guid.Empty);
    await Assert.That(cascaded.InvoiceId.ToGuid()).IsEqualTo(Guid.Empty)
      .Because("the id takes only UUIDv7, and the version 4 stream it would inherit is refused rather than thrown");
  }

  [Test]
  public async Task AGuidSource_IsInheritedThroughFromAsync() {
    var cascaded = new InheritanceInvoiceRaised();

    var tracked = await _cascadeAsync(cascaded, new NoteInheritanceOrder { OrderId = _v7 });

    await Assert.That(tracked).IsEqualTo(_v7);
    await Assert.That(cascaded.InvoiceId.ToGuid()).IsEqualTo(_v7);
  }

  [Test]
  public async Task ASourceTheStreamIdLists_IsInheritedAsync() {
    var cascaded = new InheritanceInvoiceFromOrder();

    var tracked = await _cascadeAsync(cascaded, _orderSource());

    await Assert.That(tracked).IsEqualTo(_v7);
    await Assert.That(cascaded.InvoiceId.ToGuid()).IsEqualTo(_v7)
      .Because("InheritFrom names OrderId, so the invoice event joins the order's stream");
  }

  [Test]
  public async Task AfterARefusal_AnEventWithGenerateStreamId_MintsItsOwnIdAsync() {
    var refused = new InheritanceInvoiceMinted();
    var sourceless = new InheritanceInvoiceMinted();

    var trackedRefused = await _cascadeAsync(refused, _orderSource());
    var trackedSourceless = await _cascadeAsync(sourceless, source: null);

    await Assert.That(refused.InvoiceId.ToGuid()).IsNotEqualTo(Guid.Empty);
    await Assert.That(refused.InvoiceId.ToGuid()).IsNotEqualTo(_v7)
      .Because("the refused source stream is not used");
    await Assert.That(trackedRefused).IsEqualTo(refused.InvoiceId.ToGuid())
      .Because("the minted id is the stream, as it is for a cascade with no source stream");
    await Assert.That(trackedSourceless).IsEqualTo(sourceless.InvoiceId.ToGuid());
    await Assert.That(sourceless.InvoiceId.ToGuid()).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task AfterARefusal_AnEventWithoutGenerateStreamId_HasNoStreamIdAsync() {
    var refused = new InheritanceInvoiceRaised();
    var sourceless = new InheritanceInvoiceRaised();

    var trackedRefused = await _cascadeAsync(refused, _orderSource());
    var trackedSourceless = await _cascadeAsync(sourceless, source: null);

    await Assert.That(trackedRefused).IsEqualTo(trackedSourceless)
      .Because("a refused source leaves the event as a cascade with no source stream leaves it");
    await Assert.That(refused.InvoiceId.ToGuid()).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task AGuidStreamIdTheSetterCannotWrite_StillTakesTheSourceStreamAsBeforeAsync() {
    // Only a strong-id stream id refuses: a Guid stream id the setter cannot write (init-only) is not a refusal, and the
    // envelope keeps the inherited stream as it always has
    var cascaded = new InheritanceInvoiceNoted();

    var tracked = await _cascadeAsync(cascaded, _orderSource());

    await Assert.That(tracked).IsEqualTo(_v7);
  }

  [Test]
  public async Task EachRefusedPairOfTypes_IsLoggedOnceAsync() {
    var logs = new List<(LogLevel Level, string Message)>();
    var dispatcher = _createDispatcher(logs);

    await _cascadeAsync(dispatcher, new InheritanceInvoiceRaised(), _orderSource());
    await _cascadeAsync(dispatcher, new InheritanceInvoiceRaised(), _orderSource());
    await _cascadeAsync(dispatcher, new InheritanceInvoiceRaised(), new NoteInheritanceOrder { OrderId = _v4 });

    var refusals = logs.Where(l => l.Message.Contains("not inherited", StringComparison.Ordinal)).ToList();
    await Assert.That(refusals.Count).IsEqualTo(2)
      .Because("each pair of source and target types is logged the first time only, however often it recurs");
    await Assert.That(refusals.All(r => r.Level == LogLevel.Warning)).IsTrue();
    await Assert.That(refusals[0].Message).Contains(nameof(PlaceInheritanceOrder));
    await Assert.That(refusals[0].Message).Contains(nameof(InheritanceInvoiceRaised));
    await Assert.That(refusals[1].Message).Contains(nameof(NoteInheritanceOrder));
  }

  /// <summary>
  /// Cascades <paramref name="message"/> from <paramref name="source"/> as an outbox event on a fresh dispatcher, and
  /// returns the stream it was tracked under: the stream the dispatcher resolved for its envelope.
  /// </summary>
  private static Task<Guid> _cascadeAsync(IMessage message, object? source) =>
    _cascadeAsync(_createDispatcher(logs: null), message, source);

  private static async Task<Guid> _cascadeAsync(Core.Dispatcher dispatcher, IMessage message, object? source) {
    var tracker = new ScopedEventTracker();
    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      await dispatcher.CascadeMessageAsync(message, source is null ? null : _sourceEnvelope(source), DispatchModes.Outbox);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
    return tracker.GetEmittedEvents().Single(e => e.EventType == message.GetType()).StreamId;
  }

  private static Core.Dispatcher _createDispatcher(List<(LogLevel Level, string Message)>? logs) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (logs is not null) {
      services.AddLogging(builder => {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(new ListLoggerProvider(logs));
      });
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return (Core.Dispatcher)services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  private sealed class ListLoggerProvider(List<(LogLevel Level, string Message)> sink) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new ListLogger(sink);

    public void Dispose() {
      // The sink belongs to the test.
    }

    private sealed class ListLogger(List<(LogLevel Level, string Message)> sink) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (sink) {
          sink.Add((logLevel, formatter(state, exception)));
        }
      }
    }
  }
}
