// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that runs the managed-object reconcile between starts, so a drop held back at a start
/// (the previous release was still running and declared the object) happens once that release is gone, without
/// waiting for the next deploy.
/// </summary>
/// <remarks>
/// <para>
/// Run once per fleet per window: only the instance that wins the window's claim in
/// <c>wh_unique_emission_claims</c> (the claim <c>PublishOnceAsync</c> uses) reconciles, and the reconcile itself
/// takes the schema lock with a try, so it never waits on a start that is migrating. A host with no claim store
/// skips the step rather than have every instance reconcile every cycle; its starts still reconcile.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose manifest this step reconciles.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs</tests>
public sealed partial class ManagedSchemaReconcileStep(
    Type dbContextType,
    ILogger<ManagedSchemaReconcileStep>? logger = null,
    TimeProvider? timeProvider = null) : IMaintenanceStep {

  /// <summary>How long one instance's claim to run the step lasts: the fleet reconciles once per window.</summary>
  public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(15);

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "managed-schema-objects";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetKeyedService<ManagedSchemaManifest>(_dbContextType) is not { } manifest) {
      LogNoManifest(_logger, _dbContextType.Name);
      return;
    }
    var settings = ManagedSchemaReconcile.Settings(services);
    if (settings.Mode == ReconcileMode.Off) {
      return;
    }
    if (services.GetService<IClaimedEmissionStore>() is not { } claims) {
      LogNoClaimStore(_logger);
      return;
    }

    var now = _timeProvider.GetUtcNow();
    var window = now.UtcTicks - (now.UtcTicks % ClaimWindow.Ticks);
    var key = string.Create(CultureInfo.InvariantCulture, $"whizbang:managed-schema-objects:{manifest.Schema}:{window}");
    if (!await claims.TryClaimAsync(key, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)) {
      return;
    }

    var context = (DbContext)services.GetRequiredService(_dbContextType);
    await ManagedSchemaReconcile.RunAsync(
      context, manifest.Schema, manifest.Declare(), settings,
      SchemaBoundaryConnections.Resolve(context, initConnectionString: null, services), services, _logger,
      cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Managed-object step skipped: {DbContext} registered no managed-object manifest")]
  private static partial void LogNoManifest(ILogger logger, string dbContext);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Managed-object step skipped: no claim store is registered to run it on one instance")]
  private static partial void LogNoClaimStore(ILogger logger);
}
