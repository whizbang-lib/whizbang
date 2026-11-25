// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Opens this process's strong-id write form once the fleet reads it, and carries the strong-id stored-form rewrite on
/// from where startup left it, a slice per maintenance cycle, until every table is settled (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Each cycle asks the schema's strong-id document gate (<see cref="StrongIdDocumentGate"/>): once every live instance
/// reads a strongly-typed id stored as its UUID string, this process writes it that way
/// (<see cref="Whizbang.Core.Perspectives.WhizbangIdDocumentForm"/>), and says so in its heartbeat.
/// </para>
/// <para>
/// Startup runs one slice of <see cref="StrongIdScalarRewrite"/> within its budget; a table larger than that is finished
/// here, from the key the journal recorded, each batch under the rewrite's own lock (<see cref="BatchedRewriteLockKey"/>),
/// so two instances never convert the same rows and a starting instance never waits on a batch. It waits until every
/// live instance writes the UUID string, so none writes the object form behind it.
/// </para>
/// <para>
/// Once every table is settled the step remembers it for the process and does nothing more: the model, and so the
/// tables, do not change while it runs.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose perspectives the step converts.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the slice's budget is measured on.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs:TheMaintenanceStep_FinishesWhatStartupLeft_ThenDoesNothingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public sealed class StrongIdScalarRewriteStep(
    Type dbContextType,
    ILogger<StrongIdScalarRewriteStep>? logger = null,
    TimeProvider? timeProvider = null) : IMaintenanceStep {
  /// <summary>The step's name in logs.</summary>
  public const string STEP_NAME = "strong-id-scalar-rewrite";

  /// <summary>The context types whose tables are all settled, and whose schema's gate is open, in this process.</summary>
  private static readonly ConcurrentDictionary<Type, bool> _settled = new();

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => STEP_NAME;

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (_settled.ContainsKey(_dbContextType)) {
      return;
    }

    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    schema = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
    var rewrites = StrongIdScalarRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, schema)
      .Select(t => (t.Name, t.Sql)).ToList();

    // The rewrite writes as the role startup migrations use, when one is configured.
    var connectionString = SchemaInitConnection.For(services, _dbContextType) ?? context.Database.GetConnectionString()
      ?? throw new InvalidOperationException($"{TypeNameFormatter.SimpleName(_dbContextType)} has no connection string to rewrite with.");
    var libraryVersion = services.GetService<ILibraryVersionProvider>()?.LibraryVersion;
    var applier = libraryVersion is null
      ? null
      : new StoredFormApplier(libraryVersion, SchemaLedgerVersion.ApplicationVersionOf(services) ?? SchemaLedgerVersion.UNKNOWN);

    StrongIdDocumentGateAnswer gate;
    await using (var connection = new NpgsqlConnection(connectionString)) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      gate = await StrongIdDocumentGate.CheckAsync(connection, schema, cancellationToken).ConfigureAwait(false);
      StrongIdScalarRewriteStepLog.Gate(_logger, schema, gate.Status);
    }

    var run = await BatchedRewriteRunner.RunAsync(
      new BatchedRewriteRequest(
        () => new NpgsqlConnection(connectionString), schema, BatchedRewriteLockKey.Compute(schema), rewrites,
        SchemaCommandTimeout.Resolve(connectionString)),
      BatchedRewriteBudget.ForMaintenance,
      (connection, ct) => StrongIdDocumentGate.RewriteHoldAsync(connection, schema, ct),
      applier,
      _timeProvider,
      _logger,
      cancellationToken).ConfigureAwait(false);

    // Done for the process only once the schema's gate is open too: a context with nothing to rewrite still opens the
    // process's write form, which every context in the process shares.
    if (run.Outcome == BatchedRewriteOutcome.Settled && gate.State is StrongIdDocumentGateState.Open or StrongIdDocumentGateState.Ready) {
      _settled.TryAdd(_dbContextType, true);
    }
  }
}

/// <summary>Source-generated logging for the strong-id rewrite step.</summary>
internal static partial class StrongIdScalarRewriteStepLog {
  [LoggerMessage(Level = LogLevel.Debug, Message = "Strong-id document gate in {Schema}: {Status}")]
  public static partial void Gate(ILogger logger, string schema, string status);
}
