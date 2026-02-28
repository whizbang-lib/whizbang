namespace Whizbang.Core.Tracing;

/// <summary>
/// Marks a handler class for explicit tracing regardless of global settings.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a handler/receptor class, all messages processed by that handler
/// will be traced at the specified verbosity level, even when global verbosity is lower.
/// </para>
/// <para>
/// Explicit traces are captured at <see cref="TraceVerbosity.Minimal"/> or higher,
/// and are highlighted in logs with <c>[TRACE]</c> prefix and <c>Information</c> level.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Trace with default Verbose level
/// [TraceHandler]
/// public class OrderReceptor : IReceptor&lt;CreateOrder, OrderCreated&gt; {
///     public async Task&lt;OrderCreated&gt; HandleAsync(CreateOrder command, CancellationToken ct) {
///         // All invocations traced
///     }
/// }
///
/// // Trace with explicit Debug level for maximum detail
/// [TraceHandler(TraceVerbosity.Debug)]
/// public class PaymentHandler : IReceptor&lt;ProcessPayment, PaymentResult&gt; {
///     public async Task&lt;PaymentResult&gt; HandleAsync(ProcessPayment command, CancellationToken ct) {
///         // Full payload and timing traced
///     }
/// }
/// </code>
/// </example>
/// <docs>tracing/attributes</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceHandlerAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TraceHandlerAttribute : Attribute {
  /// <summary>
  /// Gets the verbosity level for this handler's traces.
  /// </summary>
  public TraceVerbosity Verbosity { get; }

  /// <summary>
  /// Creates a TraceHandler attribute with the specified verbosity.
  /// </summary>
  /// <param name="verbosity">Verbosity level for traces. Defaults to <see cref="TraceVerbosity.Verbose"/>.</param>
  public TraceHandlerAttribute(TraceVerbosity verbosity = TraceVerbosity.Verbose) {
    Verbosity = verbosity;
  }
}
