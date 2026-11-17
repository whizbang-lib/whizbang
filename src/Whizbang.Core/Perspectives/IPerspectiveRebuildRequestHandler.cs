// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Commands.System;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Carries out a rebuild request in this service: which locally registered perspectives it names, which of their
/// streams, and where the work runs (a run on the streams' owners when a multi-stream store is configured, the
/// background queue for a whole-perspective blue-green rebuild, otherwise here). It is the one handling behind both
/// ways a rebuild is asked for:
/// <list type="bullet">
/// <item>the <see cref="RebuildPerspectiveCommand"/> an operator sends, which is broadcast to every service and handled
/// by each one's receptor through this type;</item>
/// <item>the integrity audit's coverage-gap auto-repair, which calls it directly. The audit found the gap in its own
/// service's perspective, so the repair belongs to that service alone and nothing is put on the transport (#2344).</item>
/// </list>
/// Registered by both Postgres data layers. A host without one reports coverage gaps and repairs none.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#auto-repair-is-local</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.AuditRepair.cs:TheAuditsRepair_RebuildsItsOwnGap_AndPutsNothingOnTheWireAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityAuditWorkerTests.cs:LocalGaps_ReportAndRepairCappedInThisServiceAsync</tests>
public interface IPerspectiveRebuildRequestHandler {
  /// <summary>Carries out <paramref name="message"/> for the perspectives this service hosts.</summary>
  /// <param name="message">What to rebuild: perspectives (null = every registered one), streams and mode.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  ValueTask HandleAsync(RebuildPerspectiveCommand message, CancellationToken cancellationToken = default);
}
