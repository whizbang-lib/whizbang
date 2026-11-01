// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// One schema initialization at a time for a host, database and schema (#1636): the barrier every
/// <c>EnsureWhizbangDatabaseInitializedAsync</c> call passes, the host's own and a direct call alike.
/// </summary>
/// <remarks>
/// <para>
/// The host's initialization goes through <see cref="DbContextInitializationRegistry"/>, whose barrier is per host. A
/// direct <c>dbContext.EnsureWhizbangDatabaseInitializedAsync()</c> call (an application that initializes again from
/// <c>ApplicationStarted</c>, say) bypassed it and ran its own pass beside the host's, applying steps the host had not
/// reached yet. The generated method now enters this barrier first, so a second call waits for the first to finish and
/// then runs its own pass, which finds the schema current and returns on the fast path.
/// </para>
/// <para>
/// Per host, like the registry's: the host is the application service provider the context was built from (the one
/// <c>AddDbContext</c> gives it), or the service provider passed to the initialization when the context has none, keyed
/// on its <see cref="IServiceScopeFactory"/> so the root provider and its scopes are one host. Two hosts in one process
/// are two instances and are not serialized here; the schema lock excludes them, as it excludes two processes. A
/// context built by hand and initialized with no service provider has no host and passes straight through.
/// </para>
/// <para>
/// A waiter runs its own pass rather than taking the first one's outcome, because the key cannot prove the two calls
/// are about the same database: it is the connection's data source, database and schema as the connection string names
/// them.
/// </para>
/// </remarks>
/// <docs>data/turnkey-initialization#a-direct-call-waits</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationBarrierTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:ADirectDbContextInitialization_WaitsForTheHostsOwn_AndAppliesNothingAsync</tests>
public static partial class SchemaInitializationBarrier {
  private static readonly Lock _lock = new();
  private static readonly Dictionary<(object Host, string Target), Entry> _entries = [];
  private static readonly IAsyncDisposable _noHost = new Turn(null, default);

  /// <summary>The gate for one key, and how many callers hold it or wait for it.</summary>
  private sealed class Entry : IDisposable {
    public readonly SemaphoreSlim Gate = new(1, 1);
    public int Users;

    public void Dispose() => Gate.Dispose();
  }

  /// <summary>
  /// Enters the barrier for <paramref name="dbContext"/>'s host, database and schema: the host is the context's
  /// application service provider, else <paramref name="serviceProvider"/>.
  /// </summary>
  /// <param name="dbContext">The context being initialized; its connection names the database. Not opened.</param>
  /// <param name="serviceProvider">The service provider passed to the initialization, used when the context has no
  /// application service provider of its own.</param>
  /// <param name="schema">The schema being initialized.</param>
  /// <param name="logger">Where a wait is said; null says nothing.</param>
  /// <param name="cancellationToken">Ends a wait.</param>
  /// <returns>The turn, held until it is disposed.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationBarrierTests.cs:AContext_IsBarriered_ByTheHostItWasBuiltFrom_ElseByThePassedProviderAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:ADirectDbContextInitialization_WaitsForTheHostsOwn_AndAppliesNothingAsync</tests>
  public static Task<IAsyncDisposable> EnterAsync(
      DbContext dbContext, IServiceProvider? serviceProvider, string schema, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    var host = dbContext.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
      ?? serviceProvider;
    return EnterAsync(host, dbContext.Database.GetDbConnection(), schema, logger, cancellationToken);
  }

  /// <summary>
  /// Waits until no other schema initialization of the same database and schema is running in
  /// <paramref name="host"/>, and returns this call's turn; disposing it lets the next one in.
  /// </summary>
  /// <param name="host">The host's service provider; null has no host and does not wait.</param>
  /// <param name="connection">The context's connection, which names the data source and database. Not opened.</param>
  /// <param name="schema">The schema being initialized.</param>
  /// <param name="logger">Where a wait is said; null says nothing.</param>
  /// <param name="cancellationToken">Ends a wait.</param>
  /// <returns>The turn, held until it is disposed.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationBarrierTests.cs:ASecondEntry_ForTheSameHostDatabaseAndSchema_WaitsForTheFirstToLeaveAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationBarrierTests.cs:EntriesForAnotherHostSchemaOrDatabase_DoNotWaitAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationBarrierTests.cs:ACanceledWait_LeavesTheBarrierAsItFoundItAsync</tests>
  public static async Task<IAsyncDisposable> EnterAsync(
      IServiceProvider? host, DbConnection connection, string schema, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(schema);
    if (host is null) {
      return _noHost;
    }

    var key = _key(host, connection, schema);
    Entry entry;
    lock (_lock) {
      if (!_entries.TryGetValue(key, out var existing)) {
        existing = new Entry();
        _entries[key] = existing;
      }
      entry = existing;
      entry.Users++;
    }

    if (!await entry.Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) {
      LogWaiting(logger ?? NullLogger.Instance, schema, connection.Database);
      try {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        _leave(key, entry);
        throw;
      }
    }
    return new Turn(entry, key);
  }

  /// <summary>Whether a caller holds, or waits for, the turn for a host, database and schema.</summary>
  /// <param name="host">The host's service provider.</param>
  /// <param name="connection">The connection that names the data source and database.</param>
  /// <param name="schema">The schema.</param>
  /// <returns><see langword="true"/> while the key is held or waited for; a key nobody uses is forgotten.</returns>
  internal static bool Tracks(IServiceProvider host, DbConnection connection, string schema) {
    lock (_lock) {
      return _entries.ContainsKey(_key(host, connection, schema));
    }
  }

  private static (object Host, string Target) _key(IServiceProvider host, DbConnection connection, string schema) =>
    (host.GetService<IServiceScopeFactory>() ?? (object)host, string.Join('\n', connection.DataSource, connection.Database, schema));

  private static void _leave((object Host, string Target) key, Entry entry) {
    lock (_lock) {
      if (--entry.Users == 0) {
        _entries.Remove(key);
        entry.Dispose();
      }
    }
  }

  /// <summary>One caller's turn: releases the gate, once, when disposed. With no entry, there is nothing to release.</summary>
  private sealed class Turn(Entry? entry, (object Host, string Target) key) : IAsyncDisposable {
    private int _released;

    public ValueTask DisposeAsync() {
      if (entry is not null && Interlocked.Exchange(ref _released, 1) == 0) {
        entry.Gate.Release();
        _leave(key, entry);
      }
      return ValueTask.CompletedTask;
    }
  }

  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Information,
      Message = "Whizbang schema initialization of {Schema} in database {Database} is already running in this host; waiting for it to finish")]
  private static partial void LogWaiting(ILogger logger, string schema, string database);
}
