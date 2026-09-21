using Whizbang.Core.Messaging;

namespace Whizbang.Core.Priority;

/// <summary>
/// How the idle band drains: the two behaviours a class of work can opt into, and the time bounds
/// that keep a withheld bucket from starving.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkBucket.Idle"/> is work nobody waits for, so the claim withholds it while the
/// service is busy. A bucket that is withheld is a bucket that can starve, and its first occupant
/// is auditing -- audit that never runs is audit that is gone. So withholding is bounded by time,
/// never by the service happening to go quiet.
/// </para>
/// <para>
/// Two behaviours, independent, because they answer different questions. <b>Trickle</b> is "never
/// starve me": past <see cref="TrickleAfter"/> a row may be claimed even while the service is busy,
/// at most <see cref="TrickleSlice"/> of them per claim. <b>Idle drain</b> is "prefer quiet": while
/// the service reads settled, the band drains at full width. A class can have either, both, or
/// neither.
/// </para>
/// <para>
/// <see cref="ForceFullDrainAfter"/> is the floor underneath both. A service that never goes quiet
/// still empties the band, at a bounded interval, and that pass is reported distinctly -- "this
/// service never went quiet" is worth an operator's attention in its own right. It is the same
/// shape as the housekeeping deferral budget and exists for the same reason.
/// </para>
/// <para>
/// Durations rather than counts. A count of deferrals changes meaning when the poll interval moves,
/// which is a defect the housekeeping budget already carries; these bounds are cadence-independent.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/IdleBandOptionsTests.cs</tests>
public sealed class IdleBandOptions {
  private readonly HashSet<string> _trickleTypes = new(StringComparer.Ordinal);
  private readonly List<string> _trickleNamespaces = [];

  /// <summary>
  /// How long an idle row may wait before a busy service will take it anyway (default 30 minutes).
  /// Applies only to classes that opted into trickling.
  /// </summary>
  public TimeSpan TrickleAfter { get; set; } = TimeSpan.FromMinutes(30);

  /// <summary>
  /// How many rows past <see cref="TrickleAfter"/> one claim may take while the service is busy
  /// (default 10). The point of a trickle is that it is not a burst: this is deliberately far below
  /// the ordinary claim window.
  /// </summary>
  public int TrickleSlice { get; set; } = 10;

  /// <summary>
  /// How long the band may go without a full drain before one is forced regardless of activity
  /// (default 4 hours). The floor that makes withholding safe.
  /// </summary>
  public TimeSpan ForceFullDrainAfter { get; set; } = TimeSpan.FromHours(4);

  /// <summary>Type names that trickle, as the shared helper renders them.</summary>
  public IReadOnlyCollection<string> TrickleTypes => _trickleTypes;

  /// <summary>Namespaces that trickle, longest first so the most specific wins.</summary>
  public IReadOnlyList<string> TrickleNamespaces => _trickleNamespaces;

  /// <summary>Opts a message type into trickling, so it cannot wait longer than <see cref="TrickleAfter"/>.</summary>
  public IdleBandOptions TrickleType<TMessage>() where TMessage : notnull {
    // Rendered through the shared helper, never Type.FullName: a type name is a key and both
    // sides have to agree on its form (issue #698).
    _trickleTypes.Add(TypeNameFormatter.AssemblyQualifiedName(typeof(TMessage)));
    return this;
  }

  /// <summary>Opts every type under a namespace into trickling.</summary>
  public IdleBandOptions TrickleNamespace(string @namespace) {
    ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
    if (!_trickleNamespaces.Contains(@namespace, StringComparer.Ordinal)) {
      _trickleNamespaces.Add(@namespace);
      // Longest first: a rule on Foo.Bar.Baz must beat one on Foo.Bar, or the broader rule decides
      // for a type the narrower one was written for.
      _trickleNamespaces.Sort((a, b) => b.Length.CompareTo(a.Length));
    }
    return this;
  }

  /// <summary>
  /// Whether a type trickles. A type rule wins over a namespace rule; an unmatched type does not
  /// trickle, so withholding is the default and opting out of it is explicit.
  /// </summary>
  public bool Trickles(string? clrTypeName) {
    if (string.IsNullOrEmpty(clrTypeName)) {
      return false;
    }
    if (_trickleTypes.Contains(clrTypeName)) {
      return true;
    }
    foreach (var ns in _trickleNamespaces) {
      if (clrTypeName.StartsWith(ns + ".", StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }
}
