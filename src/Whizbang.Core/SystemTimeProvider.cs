namespace Whizbang.Core;

/// <summary>
/// Default implementation of <see cref="ITimeProvider"/> that delegates to
/// <see cref="TimeProvider.System"/>.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses the .NET built-in <see cref="TimeProvider.System"/> which provides:
/// </para>
/// <list type="bullet">
/// <item><description>Wall clock time via <see cref="DateTimeOffset.UtcNow"/></description></item>
/// <item><description>High-frequency timestamps via <see cref="System.Diagnostics.Stopwatch"/></description></item>
/// </list>
/// <para>
/// This class is registered as a singleton by default in <see cref="ServiceCollectionExtensions.AddWhizbang"/>.
/// </para>
/// </remarks>
/// <docs>core-concepts/time-provider</docs>
public sealed class SystemTimeProvider : ITimeProvider {
  private readonly TimeProvider _timeProvider;

  /// <summary>
  /// Initializes a new instance of <see cref="SystemTimeProvider"/> using <see cref="TimeProvider.System"/>.
  /// </summary>
  public SystemTimeProvider() : this(TimeProvider.System) {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="SystemTimeProvider"/> with a custom <see cref="TimeProvider"/>.
  /// </summary>
  /// <param name="timeProvider">The underlying time provider to use.</param>
  /// <remarks>
  /// This constructor allows wrapping a custom <see cref="TimeProvider"/> such as
  /// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> for testing scenarios.
  /// </remarks>
  public SystemTimeProvider(TimeProvider timeProvider) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    _timeProvider = timeProvider;
  }

  /// <inheritdoc />
  public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

  /// <inheritdoc />
  public DateTimeOffset GetLocalNow() => _timeProvider.GetLocalNow();

  /// <inheritdoc />
  public long GetTimestamp() => _timeProvider.GetTimestamp();

  /// <inheritdoc />
  public TimeSpan GetElapsedTime(long startingTimestamp) => _timeProvider.GetElapsedTime(startingTimestamp);

  /// <inheritdoc />
  public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
      _timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

  /// <inheritdoc />
  public long TimestampFrequency => _timeProvider.TimestampFrequency;
}
