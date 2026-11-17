// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Handles <see cref="RebuildPerspectiveCommand"/> through <see cref="PerspectiveRebuildRequestHandler"/>, the rebuild
/// handling the integrity audit's auto-repair also runs (#2344), which invokes <see cref="IPerspectiveRebuilder"/>
/// for each requested perspective. Without this receptor the command is defined but has no effect —
/// the rebuilder is never called, projections are not rebuilt, and <c>wh_perspective_cursors</c>
/// never updates. This is the missing turnkey piece between the system command and the rebuilder. Lives in the shared
/// Postgres assembly, not <c>Whizbang.Core</c> (a receptor there would make the receptor-discovery generator emit
/// dispatcher registrations that collide with every consumer's), and both data layers register it (#1959): it lived in
/// the EF Core driver, so a Dapper host that sent the command rebuilt nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fan-out:</strong> when <c>PerspectiveNames</c> is null or empty, the receptor iterates
/// every perspective in <see cref="IPerspectiveRunnerRegistry"/> and rebuilds each. Matches the
/// documented API: "Null = all registered perspectives".
/// </para>
/// <para>
/// <strong>Stream filter:</strong> when <c>IncludeStreamIds</c> is non-empty, the rebuilder's
/// <see cref="IPerspectiveRebuilder.RebuildStreamsAsync"/> runs for just those streams. When only
/// <c>ExcludeStreamIds</c> is set, the full set of streams is fetched from
/// <see cref="IEventStoreQuery"/> and the exclusions subtracted. Otherwise the mode-based method
/// (BlueGreen or InPlace) runs.
/// </para>
/// <para>
/// <strong>Unsupported fields:</strong> <c>FromEventId</c> is logged as a warning and ignored —
/// <see cref="IPerspectiveRebuilder"/> has no partial-range replay API. Adding it is tracked as
/// a follow-up.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithNamedPerspective_UpdatesCursorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithIncludeStreamIds_UpdatesOnlyTargetedCursorsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithNullPerspectiveNames_FansOutToAllRegisteredAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRebuildCommandTests.cs:ABlueGreenRebuildCommand_OnADapperHost_IsHandedToTheBackgroundWorkerAsync</tests>
public sealed class RebuildPerspectiveCommandReceptor : IReceptor<RebuildPerspectiveCommand> {
  private readonly PerspectiveRebuildRequestHandler _handler;

  /// <summary>Creates the receptor over the shared rebuild handling, logging under this receptor's category.</summary>
  /// <param name="scopeFactory">Scope factory the handling resolves the driver-scoped rebuild services from.</param>
  /// <param name="logger">Logger.</param>
  public RebuildPerspectiveCommandReceptor(IServiceScopeFactory scopeFactory, ILogger<RebuildPerspectiveCommandReceptor> logger) =>
    _handler = new PerspectiveRebuildRequestHandler(scopeFactory, logger);

  /// <summary>Creates the receptor over the given rebuild handling, the one the integrity audit runs a repair through.</summary>
  /// <param name="handler">The shared rebuild handling.</param>
  public RebuildPerspectiveCommandReceptor(PerspectiveRebuildRequestHandler handler) {
    ArgumentNullException.ThrowIfNull(handler);
    _handler = handler;
  }

  /// <inheritdoc />
  public ValueTask HandleAsync(RebuildPerspectiveCommand message, CancellationToken cancellationToken = default) =>
    _handler.HandleAsync(message, cancellationToken);
}
