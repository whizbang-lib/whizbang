namespace Whizbang.Testing;

/// <summary>
/// Centralized timeout scaling for integration tests.
/// CI environments (GitHub Actions, Azure DevOps) are slower than local Docker —
/// set WHIZBANG_TEST_TIMEOUT_MULTIPLIER to scale all completion-signal timeouts.
/// </summary>
/// <remarks>
/// This does NOT change the completion-signal pattern — tests still use TaskCompletionSource
/// and only wait for actual completion. The multiplier just extends the safety-net timeout
/// for environments where message delivery is slower (ASB emulator on shared CI runners).
/// </remarks>
public static class TestTimeouts {
  /// <summary>
  /// Multiplier applied to all test timeout values.
  /// Default: 1 (no scaling). Set WHIZBANG_TEST_TIMEOUT_MULTIPLIER env var to override.
  /// </summary>
  public static int Multiplier { get; } = int.TryParse(
    Environment.GetEnvironmentVariable("WHIZBANG_TEST_TIMEOUT_MULTIPLIER"), out var m) && m > 0 ? m : 1;

  /// <summary>
  /// Scales a timeout value by the multiplier.
  /// </summary>
  public static int Scale(int timeoutMilliseconds) => timeoutMilliseconds * Multiplier;

  /// <summary>
  /// Scales a timeout value by the multiplier.
  /// </summary>
  public static TimeSpan Scale(TimeSpan timeout) => timeout * Multiplier;
}
