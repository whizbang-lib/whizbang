namespace Whizbang.Core.Notifications.AppSignals;

/// <summary>
/// App-level pub/sub built on top of postgres NOTIFY/LISTEN. Strictly isolated from
/// Whizbang's internal <c>wh_work</c> channel — app code cannot publish to or subscribe
/// to internal categories, and internal listeners ignore app traffic. App topics live
/// on dedicated channels named <c>wh_app_&lt;topic&gt;</c>.
/// Phase D of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public interface IAppSignalChannel {
  /// <summary>
  /// Publish a payload to all subscribers of <paramref name="topic"/>. The notification
  /// fires on the postgres connection's COMMIT (so it can be transactionally tied to other
  /// writes by sharing a transaction). Topics must match
  /// <c>^[a-z][a-z0-9_]{0,62}$</c> and must not start with <c>wh_</c> (reserved).
  /// </summary>
  /// <exception cref="ArgumentException">If topic format is invalid.</exception>
  Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default);

  /// <summary>
  /// Subscribe to a topic. The handler is invoked once per delivered notification.
  /// Dispose the returned <see cref="IDisposable"/> to unsubscribe.
  /// </summary>
  IDisposable Subscribe(string topic, Func<string, CancellationToken, Task> handler);
}

/// <summary>
/// Validates app-signal topic names. Internal <c>wh_</c> prefix is reserved; reject.
/// </summary>
public static class AppSignalTopicValidator {
  // Compiled regex with a hard 100ms timeout. The pattern itself is linear and bounded
  // ({0,62}) so it can't ReDoS, but Sonar S6444 (and defense-in-depth) wants a timeout
  // on every untrusted-input regex. Topic names are caller-supplied so we honor that.
  private static readonly System.Text.RegularExpressions.Regex _topicPattern = new(
    @"^[a-z][a-z0-9_]{0,62}$",
    System.Text.RegularExpressions.RegexOptions.Compiled,
    TimeSpan.FromMilliseconds(100));

  /// <summary>Returns the validated topic; throws if invalid or reserved.</summary>
  public static string Validate(string topic) {
    if (string.IsNullOrWhiteSpace(topic)) {
      throw new ArgumentException("Topic must be non-empty", nameof(topic));
    }
    if (topic.StartsWith("wh_", StringComparison.Ordinal)) {
      throw new ArgumentException(
        $"Topic '{topic}' is invalid: the 'wh_' prefix is reserved for Whizbang internal signals",
        nameof(topic));
    }
    if (!_topicPattern.IsMatch(topic)) {
      throw new ArgumentException(
        $"Topic '{topic}' is invalid: must match ^[a-z][a-z0-9_]{{0,62}}$",
        nameof(topic));
    }
    return topic;
  }

  /// <summary>Builds the postgres channel name from a topic ('wh_app_' prefix).</summary>
  public static string ToChannelName(string topic) => "wh_app_" + Validate(topic);
}

/// <summary>No-op app signal channel used when notifications are disabled.</summary>
public sealed class NoOpAppSignalChannel : IAppSignalChannel {
  /// <inheritdoc />
  public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default) {
    _ = AppSignalTopicValidator.Validate(topic);  // still validate for caller correctness
    return Task.CompletedTask;
  }
  /// <inheritdoc />
  public IDisposable Subscribe(string topic, Func<string, CancellationToken, Task> handler) {
    _ = AppSignalTopicValidator.Validate(topic);
    return new NoOpDisposable();
  }

  private sealed class NoOpDisposable : IDisposable {
    public void Dispose() { }
  }
}
