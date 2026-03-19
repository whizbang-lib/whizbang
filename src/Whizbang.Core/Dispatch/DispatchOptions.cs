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
/// <docs>fundamentals/dispatcher/dispatcher#dispatch-options</docs>
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

  /// <summary>
  /// When true, LocalInvokeAsync waits for all perspectives to finish processing
  /// any cascaded events before returning. Default is false.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use this option for RPC-style calls where you need to ensure all perspectives
  /// have processed the events before the response is returned to the caller.
  /// </para>
  /// <para>
  /// This uses <see cref="Whizbang.Core.Perspectives.Sync.IEventCompletionAwaiter"/>
  /// internally to wait for all perspectives.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Default_WaitForPerspectives_IsFalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WaitForPerspectives_PropertySetter_WorksAsync</tests>
  public bool WaitForPerspectives { get; set; }

  /// <summary>
  /// Timeout for waiting for perspectives to finish processing.
  /// Only used when <see cref="WaitForPerspectives"/> is true.
  /// Default is 30 seconds.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:Default_PerspectiveWaitTimeout_Is30SecondsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:PerspectiveWaitTimeout_PropertySetter_WorksAsync</tests>
  public TimeSpan PerspectiveWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Enables waiting for all perspectives to process cascaded events before returning.
  /// </summary>
  /// <param name="timeout">Optional custom timeout. Default is 30 seconds.</param>
  /// <returns>This options instance for fluent chaining.</returns>
  /// <remarks>
  /// <para>
  /// Use this for RPC-style calls where you need to ensure all perspectives
  /// have processed the events before the response is returned.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Wait for perspectives with default timeout (30s)
  /// var options = new DispatchOptions().WithPerspectiveWait();
  ///
  /// // Wait for perspectives with custom timeout
  /// var options = new DispatchOptions().WithPerspectiveWait(TimeSpan.FromMinutes(2));
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithPerspectiveWait_SetsWaitForPerspectivesToTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithPerspectiveWait_WithTimeout_SetsTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchOptionsTests.cs:WithPerspectiveWait_NoTimeout_KeepsDefaultTimeoutAsync</tests>
  public DispatchOptions WithPerspectiveWait(TimeSpan? timeout = null) {
    WaitForPerspectives = true;
    if (timeout.HasValue) {
      PerspectiveWaitTimeout = timeout.Value;
    }
    return this;
  }
}
