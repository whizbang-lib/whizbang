// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Commands.System;

/// <summary>
/// System commands that all services automatically subscribe to.
/// These framework-level commands are routed via the "whizbang.system.commands" namespace.
/// </summary>
/// <remarks>
/// <para>
/// All services using SharedTopicInboxStrategy automatically include system commands
/// in their subscription filter (whizbang.system.commands.#).
/// </para>
/// <para>
/// To send a system command to all services:
/// <code>
/// await dispatcher.SendAsync(new RebuildPerspectiveCommand("OrderSummary"));
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#system-commands</docs>

/// <summary>
/// Command to rebuild one or more perspectives. Supports multiple modes and optional stream filtering.
/// </summary>
/// <param name="PerspectiveNames">Perspectives to rebuild. Null = all registered perspectives.</param>
/// <param name="Mode">Rebuild mode: BlueGreen (new table + swap), InPlace (truncate + replay).</param>
/// <param name="IncludeStreamIds">Optional: only rebuild these specific streams. Null = all streams. The list travels whole
/// to every hop (the outbox, the broadcast transport message, each receiving service's inbox), about 39 bytes of JSON
/// per id: keep it to <see cref="DEFAULT_STREAMS_PER_COMMAND"/> (500) streams or fewer and split a larger selection
/// with <see cref="SplitByStreams"/>. On Azure Service Bus Standard, more than about 6,500 ids exceeds the 256 KB
/// message limit unless large-message offloading is configured. Command audit records only the list's length.</param>
/// <param name="ExcludeStreamIds">Optional: exclude these streams from rebuild. Null = no exclusions.</param>
/// <param name="FromEventId">Optional: start replaying from this event ID. Null = from beginning.</param>
/// <param name="RequestId">Optional: a caller-chosen id stamped onto the rebuild's events. Supply one and the
/// rebuild becomes answerable: this command is broadcast to every service and each rebuilds only what it hosts,
/// so the acknowledgement cannot tell you whether any service owned the name. Finding no
/// <c>PerspectiveRebuildStarted</c> carrying this id means nothing ran. Null = each service stamps its own.</param>
/// <param name="RequestedBy">Optional: who asked, recorded on the rebuild's events.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:RebuildPerspectiveCommand_SerializesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:RebuildPerspectiveCommand_WithPerspectiveNames_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_DispatchedViaInvoker_UpdatesCursorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/RebuildPerspectiveCommandSplitTests.cs</tests>
[PinnedId("69430ea4-edbe-4a5f-890d-cd25cfe66766")]
public record RebuildPerspectiveCommand(
    string[]? PerspectiveNames = null,
    Perspectives.RebuildMode Mode = Perspectives.RebuildMode.BlueGreen,
    Guid[]? IncludeStreamIds = null,
    Guid[]? ExcludeStreamIds = null,
    long? FromEventId = null,
    Guid? RequestId = null,
    string? RequestedBy = null
) : ICommand, Messaging.IControlPlaneMessage, Audit.IBoundedCommandAudit {
  private static readonly string[] _idLists = [nameof(IncludeStreamIds), nameof(ExcludeStreamIds)];

  /// <summary>
  /// The most streams one command names when <see cref="SplitByStreams"/> splits a selection, and the documented
  /// size guidance for <see cref="IncludeStreamIds"/>: 500, the same bound a stream redelivery request uses
  /// (<see cref="Messaging.StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST"/>), about 20 KB of ids.
  /// </summary>
  /// <docs>fundamentals/perspectives/rebuild#large-stream-selections</docs>
  /// <tests>tests/Whizbang.Core.Tests/Commands/System/RebuildPerspectiveCommandSplitTests.cs:SplitByStreams_ByDefault_NamesAtMostTheRedeliveryBoundPerCommandAsync</tests>
  public const int DEFAULT_STREAMS_PER_COMMAND = Messaging.StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST;

  /// <summary>
  /// The request id of chunk <paramref name="chunkIndex"/> of a request split by <see cref="SplitByStreams"/>: the
  /// name-based (version 5) id of the chunk index within <paramref name="requestId"/>, so the same request always
  /// splits into the same ids and each chunk is its own run.
  /// </summary>
  /// <param name="requestId">The caller's request id.</param>
  /// <param name="chunkIndex">The chunk's zero-based index.</param>
  /// <returns>The chunk's request id.</returns>
  /// <docs>fundamentals/perspectives/rebuild#large-stream-selections</docs>
  /// <tests>tests/Whizbang.Core.Tests/Commands/System/RebuildPerspectiveCommandSplitTests.cs:ChunkRequestId_IsTheNameBasedIdOfTheChunkIndexWithinTheRequestAsync</tests>
  public static Guid ChunkRequestId(Guid requestId, int chunkIndex) =>
    ValueObjects.NameBasedGuid.V5(requestId, chunkIndex.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

  /// <summary>
  /// Splits this command's stream selection into commands that each name at most
  /// <paramref name="maxStreamsPerCommand"/> streams, so no one command carries an unbounded list through the outbox,
  /// the transport, each receiving service's inbox and command audit (#1525).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The streams keep the order given and a stream named twice is named once. Chunk <c>i</c> carries
  /// <see cref="ChunkRequestId"/>(<see cref="RequestId"/>, <c>i</c>) as its request id, or none when this command has
  /// none. <see cref="ExcludeStreamIds"/> is dropped from the chunks: a command that names streams ignores it.
  /// Every other field is copied.
  /// </para>
  /// <para>A command that names no streams (every stream, or an empty list) is returned unchanged, as the only element.</para>
  /// </remarks>
  /// <param name="maxStreamsPerCommand">The most streams one command names. Default <see cref="DEFAULT_STREAMS_PER_COMMAND"/>.</param>
  /// <returns>The commands to send, in order.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxStreamsPerCommand"/> is less than one.</exception>
  /// <docs>fundamentals/perspectives/rebuild#large-stream-selections</docs>
  /// <tests>tests/Whizbang.Core.Tests/Commands/System/RebuildPerspectiveCommandSplitTests.cs</tests>
  public IReadOnlyList<RebuildPerspectiveCommand> SplitByStreams(int maxStreamsPerCommand = DEFAULT_STREAMS_PER_COMMAND) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxStreamsPerCommand, 1);
    if (IncludeStreamIds is not { Length: > 0 } include) {
      return [this];
    }
    var chunks = include.Distinct().Chunk(maxStreamsPerCommand).ToArray();
    var commands = new RebuildPerspectiveCommand[chunks.Length];
    for (var i = 0; i < chunks.Length; i++) {
      commands[i] = this with {
        IncludeStreamIds = chunks[i],
        ExcludeStreamIds = null,
        RequestId = RequestId is { } requestId ? ChunkRequestId(requestId, i) : null,
      };
    }
    return commands;
  }

  /// <inheritdoc />
  /// <docs>fundamentals/security/audit-logging#bounded-command-audit</docs>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/CommandAuditListLengthsTests.cs:ARebuildNamingStreams_IsAuditedWithTheListLengths_AndKeepsTheRestOfTheCommandAsync</tests>
  public IReadOnlyCollection<string> ListsAuditedAsLengths() => _idLists;
}

/// <summary>
/// Command to cancel an in-progress perspective rebuild.
/// </summary>
/// <param name="PerspectiveName">Name of the perspective whose rebuild should be canceled.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:CancelPerspectiveRebuildCommand_CreatesCorrectlyAsync</tests>
[PinnedId("5b8636e8-5c28-4520-8013-bf1e95b7f783")]
public record CancelPerspectiveRebuildCommand(
    string PerspectiveName
) : ICommand;

/// <summary>
/// Command to clear cached data across all services.
/// </summary>
/// <param name="CacheKey">Optional specific cache key to clear. If null, clears all caches.</param>
/// <param name="CacheRegion">Optional cache region/namespace to target.</param>
/// <docs>data/caching#clear-cache</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ClearCacheCommand_WithAllParameters_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ClearCacheCommand_WithCacheKey_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ClearCacheCommand_ImplementsICommandAsync</tests>
[PinnedId("db190b57-50ca-4748-9929-0f090dba9e28")]
public record ClearCacheCommand(
    string? CacheKey = null,
    string? CacheRegion = null
) : ICommand;

/// <summary>
/// Command to collect and report diagnostics from all services.
/// </summary>
/// <param name="Type">Type of diagnostics to collect.</param>
/// <param name="CorrelationId">Optional correlation ID for tracking diagnostic responses.</param>
/// <docs>operations/observability/diagnostics#system-diagnostics</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:DiagnosticsCommand_AllDiagnosticTypes_CreateCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:DiagnosticsCommand_SerializesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:DiagnosticsCommand_ImplementsICommandAsync</tests>
[PinnedId("48bd69ed-628a-4a04-b9fc-5fa6ecd13899")]
public record DiagnosticsCommand(
    DiagnosticType Type,
    Guid? CorrelationId = null
) : ICommand;

/// <summary>
/// Type of diagnostics to collect from services.
/// </summary>
public enum DiagnosticType {
  /// <summary>
  /// Basic health check - is the service responsive?
  /// </summary>
  HealthCheck,

  /// <summary>
  /// Memory usage, thread count, and resource metrics.
  /// </summary>
  ResourceMetrics,

  /// <summary>
  /// Current state of message processing pipelines.
  /// </summary>
  PipelineStatus,

  /// <summary>
  /// Perspective and projection state information.
  /// </summary>
  PerspectiveStatus,

  /// <summary>
  /// Full diagnostic dump including all above categories.
  /// </summary>
  Full
}

/// <summary>
/// Command to pause message processing across all services.
/// Used for coordinated maintenance operations.
/// </summary>
/// <param name="DurationSeconds">Optional duration in seconds after which processing resumes automatically.</param>
/// <param name="Reason">Reason for pausing (for logging/audit).</param>
/// <docs>fundamentals/lifecycle/lifecycle#pause-resume</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:PauseProcessingCommand_WithAllParameters_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:PauseProcessingCommand_WithDuration_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:PauseProcessingCommand_ImplementsICommandAsync</tests>
[PinnedId("7475daa0-8b26-40fe-af25-215ac42e2526")]
public record PauseProcessingCommand(
    int? DurationSeconds = null,
    string? Reason = null
) : ICommand;

/// <summary>
/// Command to resume message processing across all services.
/// </summary>
/// <param name="Reason">Reason for resuming (for logging/audit).</param>
/// <docs>fundamentals/lifecycle/lifecycle#pause-resume</docs>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ResumeProcessingCommand_WithReason_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ResumeProcessingCommand_DefaultParameters_CreatesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Commands/System/SystemCommandsTests.cs:ResumeProcessingCommand_ImplementsICommandAsync</tests>
[PinnedId("c50126f7-aa4f-4cb8-b46f-36f589e0c36b")]
public record ResumeProcessingCommand(
    string? Reason = null
) : ICommand;
