namespace Whizbang.Core.Dispatch;

/// <summary>
/// Configuration options for message dispatch operations.
/// Controls cancellation, timeouts, and other dispatch behaviors.
/// </summary>
/// <remarks>
/// <para>
/// Use DispatchOptions to control how messages are dispatched:
/// </para>
/// <list type="bullet">
///   <item><b>Cancellation</b>: Pass a CancellationToken to cancel long-running operations</item>
///   <item><b>Timeout</b>: Set a maximum time for the dispatch operation</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // With cancellation token
/// using var cts = new CancellationTokenSource();
/// var options = new DispatchOptions().WithCancellationToken(cts.Token);
/// await dispatcher.SendAsync(command, options);
///
/// // With timeout
/// var options = new DispatchOptions().WithTimeout(TimeSpan.FromSeconds(30));
/// await dispatcher.SendAsync(command, options);
///
/// // Chained fluent API
/// var options = new DispatchOptions()
///     .WithCancellationToken(cts.Token)
///     .WithTimeout(TimeSpan.FromMinutes(5));
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#dispatch-options</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs</tests>
public sealed class DispatchOptions {
  /// <summary>
  /// Gets a default instance with no timeout and CancellationToken.None.
  /// Each access returns a new instance to prevent shared state mutations.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Default_StaticProperty_ReturnsNewInstanceEachTimeAsync</tests>
  public static DispatchOptions Default => new();

  /// <summary>
  /// Token to cancel the dispatch operation.
  /// When cancelled, the dispatch will throw <see cref="OperationCanceledException"/>.
  /// Default is <see cref="CancellationToken.None"/>.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Default_CancellationToken_IsNone_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:CancellationToken_PropertySetter_WorksAsync</tests>
  public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

  /// <summary>
  /// Maximum time to wait for dispatch completion.
  /// When exceeded, the dispatch will throw <see cref="OperationCanceledException"/>.
  /// Null means no timeout (default).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Default_Timeout_IsNull_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Timeout_PropertySetter_WorksAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Timeout_PropertySetter_AcceptsNullAsync</tests>
  public TimeSpan? Timeout { get; set; }

  /// <summary>
  /// Sets the cancellation token for the dispatch operation.
  /// </summary>
  /// <param name="token">The cancellation token.</param>
  /// <returns>This options instance for fluent chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithCancellationToken_SetsToken_ReturnsSelfAsync</tests>
  public DispatchOptions WithCancellationToken(CancellationToken token) {
    CancellationToken = token;
    return this;
  }

  /// <summary>
  /// Sets the timeout for the dispatch operation.
  /// </summary>
  /// <param name="timeout">The timeout duration. Must be non-negative.</param>
  /// <returns>This options instance for fluent chaining.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when timeout is negative.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithTimeout_SetsTimeout_ReturnsSelfAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithTimeout_ZeroValue_SetsTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithTimeout_NegativeValue_ThrowsArgumentOutOfRangeExceptionAsync</tests>
  public DispatchOptions WithTimeout(TimeSpan timeout) {
    ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
    Timeout = timeout;
    return this;
  }
}
