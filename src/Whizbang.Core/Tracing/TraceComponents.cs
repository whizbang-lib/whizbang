namespace Whizbang.Core.Tracing;

/// <summary>
/// Flags enum specifying which components should emit trace output.
/// </summary>
/// <remarks>
/// <para>
/// TraceComponents works with <see cref="TraceVerbosity"/> to control what gets traced.
/// Verbosity controls the detail level, while components control which parts of the system
/// generate output.
/// </para>
/// <para>
/// Configuration can specify multiple components via comma-delimited strings:
/// <c>"Http, Handlers, EventStore"</c>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Configuration via appsettings.json
/// {
///   "Whizbang": {
///     "Tracing": {
///       "Components": ["Http", "Handlers", "EventStore"]
///     }
///   }
/// }
///
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Tracing.Components = TraceComponents.Http
///                              | TraceComponents.Handlers;
/// });
///
/// // Checking component flags
/// if (options.Components.HasFlag(TraceComponents.Handlers)) {
///   // Emit handler trace
/// }
/// </code>
/// </example>
/// <docs>tracing/components</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceComponentsTests.cs</tests>
[Flags]
public enum TraceComponents {
  /// <summary>
  /// No component tracing enabled.
  /// </summary>
  None = 0,

  /// <summary>
  /// HTTP requests and responses at the API boundary.
  /// </summary>
  Http = 1,

  /// <summary>
  /// Command creation, dispatch, and completion.
  /// </summary>
  Commands = 2,

  /// <summary>
  /// Event creation, publishing, and cascading.
  /// </summary>
  Events = 4,

  /// <summary>
  /// Outbox write and delivery operations.
  /// </summary>
  Outbox = 8,

  /// <summary>
  /// Inbox message consumption and processing.
  /// </summary>
  Inbox = 16,

  /// <summary>
  /// Event store read and write operations.
  /// </summary>
  EventStore = 32,

  /// <summary>
  /// Handler discovery and invocation.
  /// </summary>
  Handlers = 64,

  /// <summary>
  /// Lifecycle stage transitions (PreExecute, Execute, PostExecute).
  /// </summary>
  Lifecycle = 128,

  /// <summary>
  /// Perspective updates and queries.
  /// </summary>
  Perspectives = 256,

  /// <summary>
  /// Only trace items marked with <c>[TraceHandler]</c> or <c>[TraceMessage]</c>.
  /// Use for production targeted debugging.
  /// </summary>
  Explicit = 512,

  /// <summary>
  /// All components enabled (excludes <see cref="Explicit"/>).
  /// Use for local development with full visibility.
  /// </summary>
  All = Http | Commands | Events | Outbox | Inbox | EventStore | Handlers | Lifecycle | Perspectives
}
