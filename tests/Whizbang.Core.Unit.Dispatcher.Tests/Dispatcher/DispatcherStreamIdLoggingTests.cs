// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Unit.Dispatcher.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Generating a message's stream and inheriting a source's stream run on every dispatch and cascade that needs them,
/// and each logs at debug by the types' names (#2548). The runtime caches a type's name behind a weak handle, so a step
/// that read it before checking the level allocated after every garbage collection with debug off. Each step is
/// measured with a collection before every run against the same runs without one: with debug off the difference is
/// zero, and with debug on the step is logged by the types' names.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("StreamId")]
[NotInParallel("AllocationMeasurement")]
public class DispatcherStreamIdLoggingTests {
  /// <summary>A command whose stream is generated on every dispatch.</summary>
  public sealed class StreamLoggingProbeCommand : ICommand, IHasStreamId {
    /// <summary>The stream.</summary>
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }
  }

  /// <summary>The source a cascaded event inherits its stream from.</summary>
  /// <param name="StreamId">The stream.</param>
  public sealed record StreamLoggingSourceEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>A cascaded event that names no stream of its own.</summary>
  public sealed class StreamLoggingInheritingEvent : IEvent, IHasStreamId {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid StreamId { get; set; }
  }

  [Test]
  public async Task GeneratingAStream_WithDebugOff_ReadsNoTypeNameAsync() {
    var (dispatcher, _) = _createDispatcher(LogLevel.Information);
    var command = new StreamLoggingProbeCommand();

    var extra = WeakNameCacheProbe.ExtraBytesAfterCollections(
      () => dispatcher.AutoGenerateStreamIdIfNeeded(command, typeof(StreamLoggingProbeCommand)));

    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty).Because("the measured path must be the one that generates");
    await Assert.That(extra).IsEqualTo(0L);
  }

  [Test]
  public async Task GeneratingAStream_WithDebugOn_IsLoggedByTheTypesNameAsync() {
    var (dispatcher, capture) = _createDispatcher(LogLevel.Debug);

    dispatcher.AutoGenerateStreamIdIfNeeded(new StreamLoggingProbeCommand(), typeof(StreamLoggingProbeCommand));

    await Assert.That(capture.Messages.Any(m => m.Contains("Auto-generated StreamId") && m.Contains(nameof(StreamLoggingProbeCommand))))
      .IsTrue();
  }

  [Test]
  public async Task InheritingASourcesStream_WithDebugOff_ReadsNoTypeNameAsync() {
    var (dispatcher, _) = _createDispatcher(LogLevel.Information);
    var stream = TrackedGuid.New().Value;
    var source = _sourceEnvelope(stream);
    var cascaded = new StreamLoggingInheritingEvent();

    var extra = WeakNameCacheProbe.ExtraBytesAfterCollections(
      () => dispatcher.InheritSourceStreamId(cascaded, typeof(StreamLoggingInheritingEvent), source));

    await Assert.That(cascaded.StreamId).IsEqualTo(stream).Because("the measured path must be the one that inherits");
    await Assert.That(extra).IsEqualTo(0L);
  }

  [Test]
  public async Task InheritingASourcesStream_WithDebugOn_IsLoggedByBothTypesNamesAsync() {
    var (dispatcher, capture) = _createDispatcher(LogLevel.Debug);

    dispatcher.InheritSourceStreamId(new StreamLoggingInheritingEvent(), typeof(StreamLoggingInheritingEvent),
      _sourceEnvelope(TrackedGuid.New().Value));

    await Assert.That(capture.Messages.Any(m => m.Contains("Propagated StreamId")
        && m.Contains(nameof(StreamLoggingSourceEvent)) && m.Contains(nameof(StreamLoggingInheritingEvent))))
      .IsTrue();
  }

  private static MessageEnvelope<StreamLoggingSourceEvent> _sourceEnvelope(Guid stream) => new() {
    MessageId = MessageId.New(),
    Payload = new StreamLoggingSourceEvent(stream),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static (global::Whizbang.Core.Dispatcher Dispatcher, CapturingProvider Capture) _createDispatcher(LogLevel minimum) {
    var capture = new CapturingProvider();
    var services = new ServiceCollection();
    services.AddLogging(builder => {
      builder.SetMinimumLevel(minimum);
      builder.AddProvider(capture);
    });
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var dispatcher = (global::Whizbang.Core.Dispatcher)services.BuildServiceProvider().GetRequiredService<IDispatcher>();
    return (dispatcher, capture);
  }

  private sealed class CapturingProvider : ILoggerProvider {
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
    public ILogger CreateLogger(string categoryName) => new Sink(_messages);
    public void Dispose() { }

    private sealed class Sink(List<string> messages) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (messages) { messages.Add(formatter(state, exception)); }
      }
    }
  }
}
