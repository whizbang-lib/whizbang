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
/// Carries the strong-id stored-form rewrite on from where startup left it, a slice per maintenance cycle, until every
/// table is settled (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Startup runs one slice of <see cref="StrongIdScalarRewrite"/> within its budget; a table larger than that is finished
/// here, from the key the journal recorded, each batch under the schema lock (<see cref="BatchedRewriteRunner"/>), so two
/// instances never convert the same rows. It waits while an instance of an earlier release is live, which reads only the
/// form the rewrite replaces, and runs once none is.
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
public sealed class StrongIdScalarRewriteStep(
    Type dbContextType,
    ILogger<StrongIdScalarRewriteStep>? logger = null,
    TimeProvider? timeProvider = null) : IMaintenanceStep {
  /// <summary>The step's name in logs.</summary>
  public const string STEP_NAME = "strong-id-scalar-rewrite";

  /// <summary>The context types whose tables are all settled in this process.</summary>
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

    var run = await BatchedRewriteRunner.RunAsync(
      new BatchedRewriteRequest(
        () => new NpgsqlConnection(connectionString), schema, SchemaInitializationLockKey.Compute(schema), rewrites,
        SchemaCommandTimeout.Resolve(connectionString)),
      BatchedRewriteBudget.ForMaintenance,
      libraryVersion is null ? null : (connection, ct) => FleetVersions.OlderLiveLibraryVersionsAsync(connection, schema, libraryVersion, ct),
      applier,
      _timeProvider,
      _logger,
      cancellationToken).ConfigureAwait(false);

    if (run.Outcome == BatchedRewriteOutcome.Settled) {
      _settled.TryAdd(_dbContextType, true);
    }
  }
}
