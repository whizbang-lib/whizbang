namespace Whizbang.Core.Tracing;

/// <summary>
/// Marks a message type (event or command) for explicit tracing regardless of global settings.
/// </summary>
/// <remarks>
/// <para>
/// When applied to an event or command record/class, all handlers that process this message
/// will be traced at the specified verbosity level, even when global verbosity is lower.
/// </para>
/// <para>
/// Explicit traces are captured at <see cref="TraceVerbosity.Minimal"/> or higher,
/// and are highlighted in logs with <c>[TRACE]</c> prefix and <c>Information</c> level.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Trace all handlers that receive this event
/// [TraceMessage]
/// public sealed record ReseedSystemEvent : EventBase&lt;ReseedSystemEvent&gt; {
///     public required string OperationType { get; init; }
/// }
///
/// // Trace with Debug level for maximum detail
/// [TraceMessage(TraceVerbosity.Debug)]
/// public sealed record PaymentCompletedEvent : EventBase&lt;PaymentCompletedEvent&gt; {
///     public required Guid PaymentId { get; init; }
///     public required decimal Amount { get; init; }
/// }
/// </code>
/// </example>
/// <docs>tracing/attributes</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceMessageAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TraceMessageAttribute : Attribute {
  /// <summary>
  /// Gets the verbosity level for traces involving this message.
  /// </summary>
  public TraceVerbosity Verbosity { get; }

  /// <summary>
  /// Creates a TraceMessage attribute with the specified verbosity.
  /// </summary>
  /// <param name="verbosity">Verbosity level for traces. Defaults to <see cref="TraceVerbosity.Verbose"/>.</param>
  public TraceMessageAttribute(TraceVerbosity verbosity = TraceVerbosity.Verbose) {
    Verbosity = verbosity;
  }
}
