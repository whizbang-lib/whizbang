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
}
