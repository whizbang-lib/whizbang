// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The last of a stream's events in the order a replay folds them (<see cref="ReplayEventOrder"/>), found while the events
/// are read, keeping only the candidates instead of every event (#1869). An overwrite-only perspective's rewind applies
/// only that event, so it never needs the rest in memory.
/// </summary>
/// <remarks>
/// <see cref="ReplayEventOrder"/> picks one ordering per batch from what the whole batch can prove (origin order for a
/// single-origin stream, else stream order, else commit order, else id order), and the choice is known only once every
/// event has been seen. So the last event under each ordering is kept as the events pass, and the ordering is chosen at
/// the end exactly as <see cref="ReplayEventOrder.Order"/> chooses it. Ties break as that sort breaks them: on the id's
/// canonical text, then the later event read.
/// </remarks>
/// <param name="alsoTrack">
/// Events to find the last of too, in the same ordering (<see cref="LastMatching"/>); null tracks none.
/// </param>
/// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/LastInReplayOrderTests.cs</tests>
public sealed class LastInReplayOrder(Func<MessageEnvelope<IEvent>, bool>? alsoTrack = null) {
  private readonly Candidates _last = new();
  private readonly Candidates _lastMatching = new();
  private Guid _origin;
  private bool _singleOrigin = true;
  private bool _allVersioned = true;
  private bool _allStamped = true;

  /// <summary>How many events were offered.</summary>
  /// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/LastInReplayOrderTests.cs:NothingOffered_HasNoLastEventAsync</tests>
  public int Count { get; private set; }

  /// <summary>The last event in replay order; null when none was offered.</summary>
  /// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/LastInReplayOrderTests.cs:Last_IsTheEventAFullReplayFoldsLastAsync</tests>
  public MessageEnvelope<IEvent>? Last => _last.Pick(this);

  /// <summary>The last event in replay order that the constructor's filter matched; null when none did.</summary>
  /// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/LastInReplayOrderTests.cs:LastMatching_IsTheLastMatchingEventInReplayOrderAsync</tests>
  public MessageEnvelope<IEvent>? LastMatching => _lastMatching.Pick(this);

  /// <summary>Takes the next event read.</summary>
  /// <param name="envelope">The event.</param>
  /// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/LastInReplayOrderTests.cs:Last_IsTheEventAFullReplayFoldsLastAsync</tests>
  public void Offer(MessageEnvelope<IEvent> envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    if (Count == 0) {
      _origin = envelope.SourceServiceId;
    }
    _singleOrigin &= _origin != Guid.Empty && envelope.SourceServiceId == _origin && envelope.SourceCommitSequence > 0;
    _allVersioned &= envelope.LocalStreamVersion.HasValue;
    _allStamped &= envelope.LocalCommitSequence.HasValue;
    _last.Offer(envelope);
    if (alsoTrack?.Invoke(envelope) == true) {
      _lastMatching.Offer(envelope);
    }
    Count++;
  }

  /// <summary>The last event under each ordering <see cref="ReplayEventOrder"/> can choose.</summary>
  private sealed class Candidates {
    private MessageEnvelope<IEvent>? _byOrigin;
    private MessageEnvelope<IEvent>? _byVersion;
    private MessageEnvelope<IEvent>? _byCommit;
    private MessageEnvelope<IEvent>? _byId;

    public void Offer(MessageEnvelope<IEvent> envelope) {
      // A later event wins a tie, as the stable sort leaves it last.
      _byOrigin = _atLeast(envelope, _byOrigin, static e => e.SourceCommitSequence) ? envelope : _byOrigin;
      _byVersion = _atLeast(envelope, _byVersion, static e => e.LocalStreamVersion.GetValueOrDefault()) ? envelope : _byVersion;
      _byCommit = _atLeast(envelope, _byCommit, static e => e.LocalCommitSequence.GetValueOrDefault()) ? envelope : _byCommit;
      _byId = _byId is null || envelope.MessageId.Value.CompareTo(_byId.MessageId.Value) >= 0 ? envelope : _byId;
    }

    public MessageEnvelope<IEvent>? Pick(LastInReplayOrder order) {
      if (order._singleOrigin) {
        return _byOrigin;
      }
      if (order._allVersioned) {
        return _byVersion;
      }
      return order._allStamped ? _byCommit : _byId;
    }

    // The ordering ReplayEventOrder sorts by: the key, then the id's canonical text.
    private static bool _atLeast(MessageEnvelope<IEvent> envelope, MessageEnvelope<IEvent>? current, Func<MessageEnvelope<IEvent>, long> key) {
      if (current is null) {
        return true;
      }
      var byKey = key(envelope).CompareTo(key(current));
      return byKey > 0 || (byKey == 0 && string.CompareOrdinal(envelope.MessageId.Value.ToString("D"), current.MessageId.Value.ToString("D")) >= 0);
    }
  }
}
