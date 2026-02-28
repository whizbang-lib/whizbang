namespace Whizbang.Core.Tracing;

/// <summary>
/// Controls the detail level of tracing output.
/// </summary>
/// <remarks>
/// <para>
/// Verbosity levels are hierarchical - each level includes everything from lower levels.
/// Explicit traces (via <c>[TraceHandler]</c> or <c>[TraceMessage]</c> attributes) are always captured
/// at <see cref="Minimal"/> or higher, regardless of the global verbosity setting.
/// </para>
/// <list type="bullet">
///   <item><b>Off</b>: No tracing output - zero overhead</item>
///   <item><b>Minimal</b>: Errors, failures, and explicit traces only</item>
///   <item><b>Normal</b>: Lifecycle stage transitions</item>
///   <item><b>Verbose</b>: Handler discovery and outbox/inbox operations</item>
///   <item><b>Debug</b>: Full payload inspection and timing breakdown</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Configuration via appsettings.json
/// {
///   "Whizbang": {
///     "Tracing": {
///       "Verbosity": "Normal"
///     }
///   }
/// }
///
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Tracing.Verbosity = TraceVerbosity.Verbose;
/// });
///
/// // Checking verbosity levels (hierarchical)
/// if (currentVerbosity >= TraceVerbosity.Normal) {
///   // Trace lifecycle transitions
/// }
/// </code>
/// </example>
/// <docs>tracing/verbosity-levels</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceVerbosityTests.cs</tests>
public enum TraceVerbosity {
  /// <summary>
  /// No tracing output. Use in production when tracing overhead must be eliminated.
  /// </summary>
  Off = 0,

  /// <summary>
  /// Errors, failures, and explicitly marked items only.
  /// Captures <c>[TraceHandler]</c> and <c>[TraceMessage]</c> attributed types.
  /// Recommended for production monitoring.
  /// </summary>
  Minimal = 1,

  /// <summary>
  /// Command/event lifecycle stage transitions.
  /// Shows message creation, dispatch, and completion without internal details.
  /// </summary>
  Normal = 2,

  /// <summary>
  /// Outbox/inbox operations and handler discovery.
  /// Shows which handlers were found and invoked for each message.
  /// </summary>
  Verbose = 3,

  /// <summary>
  /// Full payload inspection, timing breakdown, and perspective updates.
  /// Maximum detail for debugging complex scenarios.
  /// </summary>
  Debug = 4
}
