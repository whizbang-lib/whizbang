namespace Whizbang.Core.Tracing;

/// <summary>
/// Marks a type for explicit tracing. When applied, traces are always emitted
/// regardless of global verbosity settings.
/// </summary>
/// <remarks>
/// <para>
/// This attribute can be applied to:
/// <list type="bullet">
///   <item><description>Receptors (handlers) - traces all invocations of this handler</description></item>
///   <item><description>Events/Commands - traces all handlers that receive this message</description></item>
///   <item><description>Perspectives - traces perspective processing</description></item>
/// </list>
/// </para>
/// <para>
/// When a type has <c>[WhizbangTrace]</c>, the <c>whizbang.trace.explicit</c> tag
/// is set to <c>true</c> in OpenTelemetry spans, allowing easy filtering in
/// dashboards like Aspire, Jaeger, or App Insights.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Trace all invocations of this receptor
/// [WhizbangTrace]
/// public class OrderReceptor : IReceptor&lt;CreateOrder, OrderCreated&gt; { }
///
/// // Trace at Debug verbosity for more detail
/// [WhizbangTrace(Verbosity = TraceVerbosity.Debug)]
/// public class PaymentReceptor : IReceptor&lt;ProcessPayment, PaymentProcessed&gt; { }
///
/// // Trace all handlers that receive this event (future)
/// [WhizbangTrace]
/// public sealed record ReseedSystemEvent : EventBase&lt;ReseedSystemEvent&gt; { }
/// </code>
/// </example>
/// <docs>tracing/attributes</docs>
/// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithWhizbangTraceAttribute_GeneratesTracingCodeAsync</tests>
[AttributeUsage(
  AttributeTargets.Class,  // Receptors, Perspectives, Events, Commands
  AllowMultiple = false,
  Inherited = false)]
public sealed class WhizbangTraceAttribute : Attribute {
  /// <summary>
  /// The verbosity level for traces from this type.
  /// Default is <see cref="TraceVerbosity.Normal"/>.
  /// </summary>
  /// <remarks>
  /// Higher verbosity levels include more detail:
  /// <list type="bullet">
  ///   <item><description><see cref="TraceVerbosity.Minimal"/> - Basic invocation only</description></item>
  ///   <item><description><see cref="TraceVerbosity.Normal"/> - Lifecycle stages</description></item>
  ///   <item><description><see cref="TraceVerbosity.Verbose"/> - Handler discovery, outbox/inbox</description></item>
  ///   <item><description><see cref="TraceVerbosity.Debug"/> - Full payload, timing breakdown</description></item>
  /// </list>
  /// </remarks>
  public TraceVerbosity Verbosity { get; init; } = TraceVerbosity.Normal;
}
