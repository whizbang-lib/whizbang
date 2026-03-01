namespace Whizbang.Core.Tracing;

/// <summary>
/// Verbosity levels for tracing output.
/// </summary>
/// <remarks>
/// Verbosity levels are hierarchical - higher levels include all output from lower levels:
/// <list type="bullet">
///   <item><description><see cref="Off"/> - No tracing</description></item>
///   <item><description><see cref="Minimal"/> - Errors and explicit traces only</description></item>
///   <item><description><see cref="Normal"/> - Lifecycle stage transitions</description></item>
///   <item><description><see cref="Verbose"/> - Handler discovery, outbox/inbox</description></item>
///   <item><description><see cref="Debug"/> - Full payload, timing breakdown</description></item>
/// </list>
/// </remarks>
/// <docs>tracing/verbosity-levels</docs>
public enum TraceVerbosity {
  /// <summary>No tracing output.</summary>
  Off = 0,

  /// <summary>Errors, failures, and explicitly marked traces only.</summary>
  Minimal = 1,

  /// <summary>Command/Event lifecycle stage transitions.</summary>
  Normal = 2,

  /// <summary>Outbox/Inbox operations, handler discovery.</summary>
  Verbose = 3,

  /// <summary>Full payload, timing breakdown, perspectives.</summary>
  Debug = 4
}
