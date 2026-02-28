namespace Whizbang.Core.Tracing;

/// <summary>
/// Marks a handler class for explicit metric collection regardless of global settings.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a handler/receptor class, metrics will always be collected for that handler
/// even when global metrics are disabled. This enables targeted performance monitoring of
/// critical handlers in production.
/// </para>
/// <para>
/// Metrics collected include:
/// <list type="bullet">
/// <item><description><c>whizbang.handler.invocations</c> - Total invocations</description></item>
/// <item><description><c>whizbang.handler.successes</c> - Successful completions</description></item>
/// <item><description><c>whizbang.handler.failures</c> - Failed invocations</description></item>
/// <item><description><c>whizbang.handler.duration</c> - Execution time histogram</description></item>
/// <item><description><c>whizbang.handler.active</c> - Currently executing count</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Always collect metrics for this handler
/// [MetricHandler]
/// public class OrderReceptor : IReceptor&lt;CreateOrder, OrderCreated&gt; {
///     public async Task&lt;OrderCreated&gt; HandleAsync(CreateOrder command, CancellationToken ct) {
///         // Metrics always collected
///     }
/// }
///
/// // Combine with tracing for full observability
/// [TraceHandler]
/// [MetricHandler]
/// public class PaymentHandler : IReceptor&lt;ProcessPayment, PaymentResult&gt; {
///     public async Task&lt;PaymentResult&gt; HandleAsync(ProcessPayment command, CancellationToken ct) {
///         // Both traced and metered
///     }
/// }
/// </code>
/// </example>
/// <docs>tracing/attributes</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/MetricHandlerAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MetricHandlerAttribute : Attribute {
  /// <summary>
  /// Creates a MetricHandler attribute.
  /// </summary>
  public MetricHandlerAttribute() { }
}
