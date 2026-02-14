namespace Whizbang.Core;

/// <summary>
/// Provides an abstraction for time-related operations.
/// Wraps the .NET <see cref="System.TimeProvider"/> to enable testability and future enhancements
/// such as caching or custom time sources.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation <see cref="SystemTimeProvider"/> delegates to
/// <see cref="System.TimeProvider.System"/> which uses:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="DateTimeOffset.UtcNow"/> for wall clock time</description></item>
/// <item><description><see cref="System.Diagnostics.Stopwatch"/> for high-frequency timestamps</description></item>
/// </list>
/// <para>
/// For testing, inject a mock or fake implementation to control time.
/// </para>
/// </remarks>
/// <docs>core-concepts/time-provider</docs>
public interface ITimeProvider {
  /// <summary>
  /// Gets the current UTC date and time.
  /// </summary>
  /// <returns>The current UTC time as a <see cref="DateTimeOffset"/>.</returns>
  DateTimeOffset GetUtcNow();

  /// <summary>
  /// Gets the current local date and time based on the system's local time zone.
  /// </summary>
  /// <returns>The current local time as a <see cref="DateTimeOffset"/>.</returns>
  DateTimeOffset GetLocalNow();

  /// <summary>
  /// Gets the current high-frequency timestamp for measuring elapsed time.
  /// Uses <see cref="System.Diagnostics.Stopwatch"/> internally for high precision.
  /// </summary>
  /// <returns>A high-frequency timestamp value.</returns>
  long GetTimestamp();

  /// <summary>
  /// Gets the elapsed time between a starting timestamp and now.
  /// </summary>
  /// <param name="startingTimestamp">The starting timestamp obtained from <see cref="GetTimestamp"/>.</param>
  /// <returns>The elapsed time as a <see cref="TimeSpan"/>.</returns>
  TimeSpan GetElapsedTime(long startingTimestamp);

  /// <summary>
  /// Gets the elapsed time between two timestamps.
  /// </summary>
  /// <param name="startingTimestamp">The starting timestamp.</param>
  /// <param name="endingTimestamp">The ending timestamp.</param>
  /// <returns>The elapsed time as a <see cref="TimeSpan"/>.</returns>
  TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

  /// <summary>
  /// Gets the frequency of the high-resolution timer (ticks per second).
  /// </summary>
  long TimestampFrequency { get; }
}
