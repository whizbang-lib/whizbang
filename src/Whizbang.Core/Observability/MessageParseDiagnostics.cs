// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Whizbang.Core.Observability;

/// <summary>Where a parsed message came from when one of its parses was recorded.</summary>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public enum MessageParseSource {
  /// <summary>A message read off a transport: a broker or wire body turned into an envelope.</summary>
  TransportReceive,

  /// <summary>A claim-checked body read back from the offload store and turned into an envelope.</summary>
  OffloadRehydration,

  /// <summary>An outbox row read back from the database by a drain or claim.</summary>
  OutboxRead,

  /// <summary>An inbox row read back from the database by a drain or claim.</summary>
  InboxRead,

  /// <summary>An event read back from the event store: a perspective drain, a stream read, a rebuild or a replay.</summary>
  EventStoreRead,

  /// <summary>A payload materialized from its JSON form so lifecycle receptors can run on it.</summary>
  LifecycleStage,

  /// <summary>A payload materialized from its JSON form so a receptor can be invoked with it.</summary>
  ReceptorDispatch,

  /// <summary>A multi-stream run's stored command read back to work one of its streams.</summary>
  MultiStreamRun,

  /// <summary>A stored message read back to be delivered again.</summary>
  Redelivery,
}

/// <summary>What part of a message one recorded parse produced.</summary>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public enum MessageParseKind {
  /// <summary>The whole envelope: text or bytes turned into an envelope whose payload is still JSON.</summary>
  Envelope,

  /// <summary>The payload: JSON turned into the message's own type.</summary>
  Payload,

  /// <summary>Stored envelope metadata (hops, dispatch context) read back apart from the payload.</summary>
  Metadata,

  /// <summary>A stored security scope read back apart from the payload.</summary>
  Scope,
}

/// <summary>One recorded parse of a message.</summary>
/// <param name="MessageId">The message whose envelope or payload was parsed.</param>
/// <param name="Source">Where the parsed message came from.</param>
/// <param name="Kind">What part of the message the parse produced.</param>
/// <param name="Member">The member that parsed it.</param>
/// <param name="FilePath">The source file of the parse site, as the compiler recorded it.</param>
/// <param name="Line">The line of the parse site.</param>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public readonly record struct MessageParse(
    Guid MessageId,
    MessageParseSource Source,
    MessageParseKind Kind,
    string Member,
    string FilePath,
    int Line) {
  /// <summary>The parse site as <c>File.cs:line</c>, or the member name when no file was recorded.</summary>
  public string Site {
    get {
      if (string.IsNullOrEmpty(FilePath)) {
        return Member;
      }
      // The compiler records the path of the machine that built the library, in its own separator.
      var name = FilePath[(FilePath.LastIndexOfAny(['/', '\\']) + 1)..];
      return $"{name}:{Line}";
    }
  }
}

/// <summary>Sees every message parse the framework records while it is subscribed.</summary>
/// <remarks>
/// Called on the thread that parsed, inside the parse path, so it must be fast and must not throw: an
/// exception propagates into the code that was reading the message.
/// </remarks>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public interface IMessageParseObserver {
  /// <summary>Called once for each recorded parse.</summary>
  /// <param name="parse">The parse.</param>
  void OnParse(in MessageParse parse);
}

/// <summary>
/// The process-wide seam through which the framework reports each time it deserializes a message's envelope or
/// payload: by message id, by where the message came from, by what was parsed, and by the site that parsed it.
/// </summary>
/// <remarks>
/// <para>
/// It exists to measure how many times one message is parsed on its way through a process (once per perspective,
/// once per lifecycle stage, once per stream of a multi-stream run), and to pin those counts in regression tests so
/// that a change which adds a parse is seen. It is diagnostics, never a decision input: nothing in the framework
/// reads it.
/// </para>
/// <para>
/// It costs one volatile read per parse site when nothing is subscribed. The call-site arguments are compile-time
/// constants, so a site does not allocate to report.
/// </para>
/// </remarks>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public static class MessageParseDiagnostics {
  private static IMessageParseObserver[] _observers = [];

  /// <summary>Whether any observer is subscribed.</summary>
  public static bool IsEnabled => Volatile.Read(ref _observers).Length > 0;

  /// <summary>Subscribes an observer to every parse recorded from now until the returned handle is disposed.</summary>
  /// <param name="observer">The observer.</param>
  /// <returns>The subscription; dispose it to unsubscribe.</returns>
  public static IDisposable Subscribe(IMessageParseObserver observer) {
    ArgumentNullException.ThrowIfNull(observer);
    _update(current => [.. current, observer]);
    return new Subscription(observer);
  }

  /// <summary>Records one parse of a message. Called by the framework at each place it deserializes a message.</summary>
  /// <param name="messageId">The message whose envelope or payload was parsed.</param>
  /// <param name="source">Where the message came from.</param>
  /// <param name="kind">What part of the message the parse produced.</param>
  /// <param name="member">Supplied by the compiler.</param>
  /// <param name="filePath">Supplied by the compiler.</param>
  /// <param name="line">Supplied by the compiler.</param>
  public static void Record(
      Guid messageId,
      MessageParseSource source,
      MessageParseKind kind,
      [CallerMemberName] string member = "",
      [CallerFilePath] string filePath = "",
      [CallerLineNumber] int line = 0) {
    var observers = Volatile.Read(ref _observers);
    if (observers.Length == 0) {
      return;
    }
    var parse = new MessageParse(messageId, source, kind, member, filePath, line);
    foreach (var observer in observers) {
      observer.OnParse(in parse);
    }
  }

  private static void _update(Func<IMessageParseObserver[], IMessageParseObserver[]> change) {
    while (true) {
      var current = Volatile.Read(ref _observers);
      if (ReferenceEquals(Interlocked.CompareExchange(ref _observers, change(current), current), current)) {
        return;
      }
    }
  }

  private sealed class Subscription(IMessageParseObserver observer) : IDisposable {
    private int _disposed;

    public void Dispose() {
      if (Interlocked.Exchange(ref _disposed, 1) == 1) {
        return;
      }
      _update(current => Array.FindAll(current, o => !ReferenceEquals(o, observer)));
    }
  }
}

/// <summary>One line of a parse summary: how many times one site parsed one part of a message.</summary>
/// <param name="Kind">What part of the message was parsed.</param>
/// <param name="Source">Where the message came from.</param>
/// <param name="Site">The parse site as <c>File.cs:line</c>.</param>
/// <param name="Count">How many times.</param>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public readonly record struct MessageParseSiteCount(MessageParseKind Kind, MessageParseSource Source, string Site, int Count);

/// <summary>
/// An observer that keeps the parses it sees, optionally only those of the messages it was told to track. Subscribed
/// on construction and unsubscribed on dispose.
/// </summary>
/// <remarks>
/// Filtering by message id is what lets concurrent tests, or a diagnostic session in a busy process, each count only
/// their own messages.
/// </remarks>
/// <docs>operations/observability/diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MessageParseDiagnosticsTests.cs</tests>
public sealed class MessageParseRecorder : IMessageParseObserver, IDisposable {
  private readonly ConcurrentDictionary<Guid, byte>? _tracked;
  private readonly ConcurrentQueue<MessageParse> _parses = new();
  private readonly IDisposable _subscription;

  /// <summary>Starts recording the parses of the given messages, or of every message when none are given.</summary>
  /// <param name="messageIds">The messages to record; empty to record every message.</param>
  public MessageParseRecorder(params Guid[] messageIds) {
    if (messageIds.Length > 0) {
      _tracked = new ConcurrentDictionary<Guid, byte>();
      foreach (var id in messageIds) {
        _tracked[id] = 0;
      }
    }
    _subscription = MessageParseDiagnostics.Subscribe(this);
  }

  /// <summary>Every parse recorded so far, in the order they were seen.</summary>
  public IReadOnlyList<MessageParse> Parses => [.. _parses];

  /// <summary>Adds a message to those recorded. A recorder made with no messages records every message already.</summary>
  /// <param name="messageId">The message.</param>
  public void Track(Guid messageId) {
    if (_tracked is not null) {
      _tracked[messageId] = 0;
    }
  }

  /// <summary>How many parses of a message were recorded, optionally of one kind only.</summary>
  /// <param name="messageId">The message.</param>
  /// <param name="kind">The kind to count, or null for every kind.</param>
  /// <returns>The count.</returns>
  public int CountFor(Guid messageId, MessageParseKind? kind = null) =>
    _parses.Count(p => p.MessageId == messageId && (kind is null || p.Kind == kind));

  /// <summary>The parses of a message grouped by kind, source and site, ordered by kind then site.</summary>
  /// <param name="messageId">The message.</param>
  /// <returns>One line per distinct kind, source and site.</returns>
  public IReadOnlyList<MessageParseSiteCount> Summarize(Guid messageId) =>
    [.. _parses
      .Where(p => p.MessageId == messageId)
      .GroupBy(p => (p.Kind, p.Source, p.Site))
      .Select(g => new MessageParseSiteCount(g.Key.Kind, g.Key.Source, g.Key.Site, g.Count()))
      .OrderBy(s => s.Kind)
      .ThenBy(s => s.Site, StringComparer.Ordinal)];

  /// <inheritdoc/>
  public void OnParse(in MessageParse parse) {
    if (_tracked?.ContainsKey(parse.MessageId) != false) {
      _parses.Enqueue(parse);
    }
  }

  /// <summary>Stops recording. The parses recorded so far stay readable.</summary>
  public void Dispose() => _subscription.Dispose();
}
