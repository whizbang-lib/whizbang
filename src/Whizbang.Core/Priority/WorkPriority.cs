namespace Whizbang.Core.Priority;

/// <summary>The three scheduling buckets a priority number falls in, plus the control plane outside them.</summary>
/// <docs>fundamentals/messaging/message-priority#the-number-and-the-bucket</docs>
public enum WorkBucket {
  /// <summary>A person or a synchronous caller is waiting (band 1 to 99).</summary>
  Interactive,
  /// <summary>Domain work with no one waiting (band 100 to 199).</summary>
  Standard,
  /// <summary>Work that exists because of volume or maintenance (band 200 and up).</summary>
  Background,
}

/// <summary>
/// Priority is one integer on every message, lower is more urgent. The named constants sit in the middle
/// of their band so a declaration can move in either direction without changing bucket; everything that
/// schedules (the claim's round robin and floors, lanes, reservations, meters) works on the bucket the
/// number falls in, while inside a bucket the number orders streams. Zero is "not declared": it costs
/// nothing on the wire and the receive side reads it as <see cref="STANDARD"/>, so nothing a producer leaves
/// unset can land in the urgent bucket.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-number-and-the-bucket</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs</tests>
public static class WorkPriority {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>No priority declared; read as <see cref="STANDARD"/> wherever an effective number is needed.</summary>
  public const int UNDECLARED = 0;

  /// <summary>A person or a synchronous caller is waiting. Band 1 to 99.</summary>
  public const int INTERACTIVE = 50;

  /// <summary>Domain work with no one waiting. Band 100 to 199.</summary>
  public const int STANDARD = 150;

  /// <summary>Work that exists because of volume or maintenance. Band 200 and up.</summary>
  public const int BACKGROUND = 250;

  /// <summary>Last number of the interactive band.</summary>
  public const int INTERACTIVE_BAND_END = 99;

  /// <summary>Last number of the standard band.</summary>
  public const int STANDARD_BAND_END = 199;
#pragma warning restore CA1707

  /// <summary>The bucket a number falls in; an undeclared or negative number reads as <see cref="WorkBucket.Standard"/>.</summary>
  public static WorkBucket Bucket(int priority) {
    if (!IsDeclared(priority)) {
      return WorkBucket.Standard;
    }
    if (priority <= INTERACTIVE_BAND_END) {
      return WorkBucket.Interactive;
    }
    return priority <= STANDARD_BAND_END ? WorkBucket.Standard : WorkBucket.Background;
  }

  /// <summary>The number to schedule with: the declared number, or <see cref="STANDARD"/> when none was declared.</summary>
  public static int Effective(int priority) => IsDeclared(priority) ? priority : STANDARD;

  /// <summary>Whether a number is a declaration (positive) rather than <see cref="UNDECLARED"/>.</summary>
  public static bool IsDeclared(int priority) => priority > UNDECLARED;

  /// <summary>
  /// The first of the two numbers somebody declared; <see cref="UNDECLARED"/> when neither was. The rule a
  /// seam applies when it holds a row's number and the number stored inside the row's envelope: the row is
  /// authoritative once it exists, and a row fetched before the column did falls back to the envelope.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:FirstDeclared_PrefersTheFirstNumberSomebodySetAsync</tests>
  public static int FirstDeclared(int first, int second) => IsDeclared(first) ? first : second;

  /// <summary>
  /// The most urgent (lowest) declared number in the collection; <see cref="UNDECLARED"/> when none is declared.
  /// The rule the claim folds a stream with and the default a minted composite takes from its members.
  /// </summary>
  /// <docs>fundamentals/messaging/message-priority#the-c-api</docs>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_IgnoreUndeclaredMembers_AndAgreeOnTheBandsAsync</tests>
  public static int MostUrgent(IEnumerable<int> priorities) => _fold(priorities, static (held, next) => Math.Min(held, next));
  /// <inheritdoc cref="MostUrgent(IEnumerable{int})"/>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_AcceptAnythingPrioritized_NotOnlyNumbersAsync</tests>
  public static int MostUrgent(IEnumerable<IPrioritized> items) => MostUrgent(_numbers(items));
  /// <summary>The least urgent (highest) declared number in the collection; <see cref="UNDECLARED"/> when none is declared.</summary>
  /// <docs>fundamentals/messaging/message-priority#the-c-api</docs>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_IgnoreUndeclaredMembers_AndAgreeOnTheBandsAsync</tests>
  public static int LeastUrgent(IEnumerable<int> priorities) => _fold(priorities, static (held, next) => Math.Max(held, next));
  /// <inheritdoc cref="LeastUrgent(IEnumerable{int})"/>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_AcceptAnythingPrioritized_NotOnlyNumbersAsync</tests>
  public static int LeastUrgent(IEnumerable<IPrioritized> items) => LeastUrgent(_numbers(items));
  /// <summary>The integer average of the declared numbers in the collection; <see cref="UNDECLARED"/> when none is declared.</summary>
  /// <docs>fundamentals/messaging/message-priority#the-c-api</docs>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_IgnoreUndeclaredMembers_AndAgreeOnTheBandsAsync</tests>
  public static int Average(IEnumerable<int> priorities) {
    ArgumentNullException.ThrowIfNull(priorities);
    long sum = 0;
    var declared = 0;
    foreach (var priority in priorities.Where(IsDeclared)) {
      sum += priority;
      declared++;
    }
    return declared == 0 ? UNDECLARED : (int)(sum / declared);
  }
  /// <inheritdoc cref="Average(IEnumerable{int})"/>
  /// <tests>tests/Whizbang.Core.Tests/Priority/WorkPriorityTests.cs:Folds_AcceptAnythingPrioritized_NotOnlyNumbersAsync</tests>
  public static int Average(IEnumerable<IPrioritized> items) => Average(_numbers(items));

  private static int _fold(IEnumerable<int> priorities, Func<int, int, int> pick) {
    ArgumentNullException.ThrowIfNull(priorities);
    var held = UNDECLARED;
    foreach (var priority in priorities) {
      if (!IsDeclared(priority)) {
        continue;   // nobody said; a fold never lets a blank outvote a declaration
      }
      held = IsDeclared(held) ? pick(held, priority) : priority;
    }
    return held;
  }

  private static IEnumerable<int> _numbers(IEnumerable<IPrioritized> items) {
    ArgumentNullException.ThrowIfNull(items);
    return items.Select(static item => item.Priority);
  }
}
