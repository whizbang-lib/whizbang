namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Result of analyzing a codebase for migration patterns.
/// </summary>
public sealed record AnalysisResult {
  /// <summary>
  /// Handlers found in the codebase (Wolverine IHandle implementations).
  /// </summary>
  public required IReadOnlyList<HandlerInfo> Handlers { get; init; }

  /// <summary>
  /// Projections found in the codebase (Marten projections).
  /// </summary>
  public required IReadOnlyList<ProjectionInfo> Projections { get; init; }

  /// <summary>
  /// Event store usages found in the codebase.
  /// </summary>
  public required IReadOnlyList<EventStoreUsageInfo> EventStoreUsages { get; init; }

  /// <summary>
  /// DI registrations that need to be updated.
  /// </summary>
  public required IReadOnlyList<DIRegistrationInfo> DIRegistrations { get; init; }
}

/// <summary>
/// Information about a detected Wolverine handler.
/// </summary>
/// <param name="FilePath">Path to the file containing the handler.</param>
/// <param name="ClassName">Name of the handler class.</param>
/// <param name="FullyQualifiedName">Fully qualified name of the handler class.</param>
/// <param name="MessageType">Type of message being handled.</param>
/// <param name="ReturnType">Return type of the handler (null if void).</param>
/// <param name="HandlerKind">The kind of handler pattern detected.</param>
/// <param name="LineNumber">Line number where the handler is declared.</param>
public sealed record HandlerInfo(
    string FilePath,
    string ClassName,
    string FullyQualifiedName,
    string MessageType,
    string? ReturnType,
    HandlerKind HandlerKind,
    int LineNumber);

/// <summary>
/// The kind of Wolverine handler pattern detected.
/// </summary>
public enum HandlerKind {
  /// <summary>Handler implements IHandle&lt;T&gt; interface.</summary>
  IHandleInterface,

  /// <summary>Handler has [WolverineHandler] attribute.</summary>
  WolverineAttribute,

  /// <summary>Handler follows convention (public Handle/HandleAsync method).</summary>
  ConventionBased
}

/// <summary>
/// Information about a detected Marten projection.
/// </summary>
/// <param name="FilePath">Path to the file containing the projection.</param>
/// <param name="ClassName">Name of the projection class.</param>
/// <param name="FullyQualifiedName">Fully qualified name of the projection class.</param>
/// <param name="AggregateType">Type of aggregate being projected.</param>
/// <param name="EventTypes">Event types handled by the projection.</param>
/// <param name="ProjectionKind">The kind of projection pattern detected.</param>
/// <param name="LineNumber">Line number where the projection is declared.</param>
public sealed record ProjectionInfo(
    string FilePath,
    string ClassName,
    string FullyQualifiedName,
    string AggregateType,
    IReadOnlyList<string> EventTypes,
    ProjectionKind ProjectionKind,
    int LineNumber);

/// <summary>
/// The kind of Marten projection pattern detected.
/// </summary>
public enum ProjectionKind {
  /// <summary>Single stream projection (SingleStreamProjection&lt;T&gt;).</summary>
  SingleStream,

  /// <summary>Multi-stream projection (MultiStreamProjection&lt;T&gt;).</summary>
  MultiStream,

  /// <summary>Custom projection implementation.</summary>
  Custom
}

/// <summary>
/// Information about event store usage.
/// </summary>
/// <param name="FilePath">Path to the file.</param>
/// <param name="ClassName">Class where usage was found.</param>
/// <param name="UsageKind">Kind of event store operation.</param>
/// <param name="LineNumber">Line number of the usage.</param>
public sealed record EventStoreUsageInfo(
    string FilePath,
    string ClassName,
    EventStoreUsageKind UsageKind,
    int LineNumber);

/// <summary>
/// Kind of event store usage.
/// </summary>
public enum EventStoreUsageKind {
  /// <summary>IDocumentStore injection.</summary>
  DocumentStoreInjection,

  /// <summary>IDocumentSession usage.</summary>
  DocumentSessionUsage,

  /// <summary>Event stream append operation.</summary>
  StreamAppend,

  /// <summary>Event stream read operation.</summary>
  StreamRead
}

/// <summary>
/// Information about a DI registration that needs updating.
/// </summary>
/// <param name="FilePath">Path to the file.</param>
/// <param name="RegistrationKind">Kind of registration.</param>
/// <param name="LineNumber">Line number of the registration.</param>
/// <param name="OriginalCode">The original registration code.</param>
public sealed record DIRegistrationInfo(
    string FilePath,
    DIRegistrationKind RegistrationKind,
    int LineNumber,
    string OriginalCode);

/// <summary>
/// Kind of DI registration.
/// </summary>
public enum DIRegistrationKind {
  /// <summary>AddMarten() registration.</summary>
  AddMarten,

  /// <summary>UseWolverine() registration.</summary>
  UseWolverine,

  /// <summary>Handler registration.</summary>
  HandlerRegistration
}
