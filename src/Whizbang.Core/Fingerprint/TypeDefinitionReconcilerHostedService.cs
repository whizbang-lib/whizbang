using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Fingerprint;

/// <summary>
/// Runs the <see cref="TypeDefinitionReconciler"/> once at startup, after the schema is ready. Failures are
/// logged, never fatal — a reconcile problem must not stop the app from serving; the drift is re-detected on
/// the next startup. Inert when no catalog is registered.
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
/// <remarks>Creates the hosted service.</remarks>
public sealed partial class TypeDefinitionReconcilerHostedService(
    TypeDefinitionReconciler reconciler,
    ISchemaReadyGate schemaReadyGate,
    ILogger<TypeDefinitionReconcilerHostedService> logger) : BackgroundService {
  private readonly TypeDefinitionReconciler _reconciler = reconciler;
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate;
  private readonly ILogger<TypeDefinitionReconcilerHostedService> _logger = logger;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return;
    }

    try {
      var summary = await _reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
      LogCompleted(_logger, summary.DriftDetected, summary.TypesReclassified);
    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
      // shutting down
    } catch (Exception ex) {
      LogFailed(_logger, ex);
    }
  }

  [LoggerMessage(EventId = 9214, Level = LogLevel.Information,
    Message = "Type-definition reconcile complete: {DriftDetected} drift, {TypesReclassified} type(s) reclassified.")]
  private static partial void LogCompleted(ILogger logger, int driftDetected, int typesReclassified);

  [LoggerMessage(EventId = 9215, Level = LogLevel.Error,
    Message = "Type-definition reconcile failed (non-fatal — drift will be re-detected next startup).")]
  private static partial void LogFailed(ILogger logger, Exception ex);
}
