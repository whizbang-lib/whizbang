namespace Whizbang.Core.Priority;

/// <summary>
/// Anything that carries a message priority: an envelope, an outbox or inbox row, a unit of work. One interface so
/// a caller can fold a collection of any of them with <see cref="WorkPriority.MostUrgent(System.Collections.Generic.IEnumerable{IPrioritized})"/>
/// and its siblings without projecting first.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-c-api</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_AcceptAnythingPrioritized_NotOnlyNumbersAsync</tests>
public interface IPrioritized {
  /// <summary>The number; <see cref="WorkPriority.UNDECLARED"/> when nobody set one.</summary>
  int Priority { get; }
}
