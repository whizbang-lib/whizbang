using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Base class for upsert strategies containing shared implementation logic.
/// </summary>
public abstract class BaseUpsertStrategy : IDbUpsertStrategy {
  /// <summary>
  /// When true, clears the change tracker after save to prevent entity tracking conflicts.
  /// Override in derived classes based on provider requirements.
  /// </summary>
  protected virtual bool ClearChangeTrackerAfterSave => false;

  /// <summary>
  /// Groups the row identity and payload data that flow through every upsert overload
  /// into the shared core implementation. <see cref="TableName"/> is forwarded so the
  /// atomic-UPSERT path (Path 1) can issue a raw <c>INSERT … ON CONFLICT DO UPDATE</c>
  /// against the right table without re-querying EF's model metadata.
  /// </summary>
  private readonly record struct UpsertRowArgs<TModel>(
    string TableName,
    Guid Id,
    TModel Model,
    PerspectiveMetadata Metadata,
    PerspectiveScope Scope,
    IDictionary<string, object?>? PhysicalFieldValues,
    bool ForceUpdateScope) where TModel : class;

  /// <summary>
  /// Optional Path 1 atomic-upsert hook. When set, <see cref="_upsertCoreAsync"/> attempts
  /// an atomic <c>INSERT … ON CONFLICT (id) DO UPDATE</c> using the provided
  /// <see cref="JsonSerializerOptions"/> to serialize <c>Data</c>, <c>Metadata</c>, and
  /// <c>Scope</c> as JSONB. When null (or when a row carries physical-field values), the
  /// strategy falls back to the legacy SELECT-then-INSERT/UPDATE retry path.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Consumers register a provider via their DI module — typically pointing to a
  /// generated <c>PerspectivePersistenceJsonContext.CreateOptions(...)</c> call that
  /// chains the consumer's <c>MessageJsonContext.Default</c> and
  /// <c>InfrastructureJsonContext.Default</c>. The chain MUST place a context that
  /// returns object-mode <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>
  /// for <c>[WhizbangId]</c> structs first, so EF Core 10's nested-object byte format
  /// is matched (otherwise reads through EF will throw <c>InvalidOperationException:
  /// Invalid token type</c>).
  /// </para>
  /// <para>
  /// Setting this hook is process-wide and shared by every <see cref="BaseUpsertStrategy"/>
  /// instance. The atomic path eliminates the 23505 dup-key storm slice 19's retry loop
  /// currently catches at <c>[ERR]</c> log severity.
  /// </para>
  /// </remarks>
  public static Func<JsonSerializerOptions>? PathOnePersistenceOptionsProvider { get; set; }

  /// <summary>
  /// Persistence serialization options, sourced from the cross-assembly union
  /// (<see cref="JsonContextRegistry.CreateCombinedOptions(SerializationProfile)"/> under the
  /// <see cref="SerializationProfile.Persistence"/> profile — object-mode WhizbangId for EF Core 10's
  /// jsonb byte format, aggregated across every assembly), combined with any user-supplied options from
  /// <see cref="PathOnePersistenceOptionsProvider"/> as a fallback resolver. Rebuilt per call so late
  /// assembly registrations and test-supplied options are always reflected — replacing the prior
  /// process-wide single-slot snapshot that only ever held one assembly's view (and raced across tests).
  /// </summary>
  private static JsonSerializerOptions _resolvePersistenceOptions(Func<JsonSerializerOptions>? userProvider) {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var user = userProvider?.Invoke();
    if (user?.TypeInfoResolver is null) {
      return union;
    }

    // Union first (object-mode WhizbangId + all registered persistence contexts), user options as a
    // fallback for anything the union doesn't cover. User converters are intentionally NOT copied — the
    // Persistence profile deliberately omits the scalar WhizbangId converters so object-mode wins.
    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(union.TypeInfoResolver!, user.TypeInfoResolver)
    };
  }

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, new UpsertRowArgs<TModel>(tableName, id, model, metadata, scope, null, false), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, new UpsertRowArgs<TModel>(tableName, id, model, metadata, scope, null, forceUpdateScope), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      IDictionary<string, object?> physicalFieldValues,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, new UpsertRowArgs<TModel>(tableName, id, model, metadata, scope, physicalFieldValues, false), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      IDictionary<string, object?> physicalFieldValues,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, new UpsertRowArgs<TModel>(tableName, id, model, metadata, scope, physicalFieldValues, forceUpdateScope), cancellationToken);

  /// <summary>
  /// Maximum number of retries for TOCTOU duplicate-key races.
  /// With drain-mode concurrent perspective processing, 3+ threads can race on the same row.
  /// A single retry is insufficient — the retry itself can hit 23505 if yet another thread
  /// inserts between the retry's SELECT and INSERT.
  /// </summary>
  private const int MAX_DUPLICATE_KEY_RETRIES = 3;

  private static long _duplicateKeyRetriesRecovered;

  /// <summary>
  /// Process-wide counter for TOCTOU dup-key recoveries — incremented each time the retry
  /// loop catches a 23505 and successfully recovers via UPDATE on the next pass. Exposed
  /// for diagnostics + the concurrent-upsert regression test.
  /// </summary>
  /// <docs>extending/internals/event-ordering-invariant</docs>
  public static long DuplicateKeyRetriesRecovered => Interlocked.Read(ref _duplicateKeyRetriesRecovered);

  private async Task _upsertCoreAsync<TModel>(
      DbContext context,
      UpsertRowArgs<TModel> args,
      CancellationToken cancellationToken)
      where TModel : class {
    // Per-event apply hooks: resolve once, mutate the row's data object (SetProperty) in place, and carry the
    // updated_at / version-bump decision to both write paths below. The default whizbang.timestamps hook yields
    // updated_at = now + a version bump — identical to the prior hardcoded stamping, now overridable.
    var hookPlan = PerEventApplyHooks.Resolve(new ApplyHookContext {
      ModelType = typeof(TModel),
      Scope = args.Scope,
      ApplyTimestamp = DateTimeOffset.UtcNow,
    });
    if (hookPlan.ModelFieldSetters.Count > 0) {
      PerEventApplyHooks.ApplyModelSetters(args.Model, hookPlan.ModelFieldSetters);
    }

    // TtlRow perspective-row expiry (E2-4d): a perspective whose [Ephemeral] events chose TransientStorage.TtlRow
    // gets a sliding row TTL — stamp expires_at = now + ttl on every upsert (last-activity window). The generator
    // registers the model's TTL virally; an unregistered model (PersistedRow/InMemory/Sourced) resolves to -1 and
    // its rows never expire (expires_at stays NULL).
    var ttlSeconds = Whizbang.Core.Perspectives.PerspectiveTtlRegistry.ResolveSeconds(typeof(TModel));
    DateTimeOffset? expiresAt = ttlSeconds >= 0 ? DateTimeOffset.UtcNow.AddSeconds(ttlSeconds) : null;

    // Path 1 atomic upsert. When configured (see PathOnePersistenceOptionsProvider) and
    // applicable (no physical fields, table name supplied), this single round-trip replaces
    // the SELECT-then-INSERT/UPDATE pattern and structurally eliminates the 23505 dup-key
    // race that slice 19's retry loop was built to recover from. Returns false to signal
    // the caller should fall back to the retry loop (config off, physical fields present,
    // or any other unsupported case).
    if (await _tryAtomicUpsertAsync(context, args, hookPlan, expiresAt, cancellationToken)) {
      return;
    }

    // Slice 19: retry loop for TOCTOU dup-key races. The SELECT-then-INSERT pattern in
    // _upsertCoreInnerAsync is not atomic; under concurrent perspective apply (slice 17
    // parallel consumers, rewind path re-firing post-snapshot replay) two threads can both
    // see "row absent" and both attempt INSERT. The second one hits 23505. The retry catches,
    // clears change-tracker state, and re-enters — the second-pass SELECT now sees the
    // first thread's committed row and the path goes UPDATE. After MAX retries the exception
    // propagates and the caller routes the work through the failure channel.
    for (var attempt = 0; attempt <= MAX_DUPLICATE_KEY_RETRIES; attempt++) {
      try {
        await _upsertCoreInnerAsync(context, args, hookPlan, expiresAt, cancellationToken);
        if (attempt > 0) {
          Interlocked.Increment(ref _duplicateKeyRetriesRecovered);
        }
        return;
      } catch (DbUpdateException ex) when (attempt < MAX_DUPLICATE_KEY_RETRIES && _isDuplicateKeyException(ex)) {
        // TOCTOU race: another thread inserted the row between our SELECT and INSERT.
        // Clear the failed change tracker state and retry as an UPDATE.
        context.ChangeTracker.Clear();
      }
    }
  }

  /// <summary>
  /// Attempts the Path 1 atomic <c>INSERT … ON CONFLICT (id) DO UPDATE</c> against the
  /// underlying Npgsql connection. Returns false (no-op, no SQL issued) when the path is
  /// not applicable so the caller can fall back to the legacy retry loop.
  /// </summary>
  /// <remarks>
  /// Fall-back conditions: <see cref="PathOnePersistenceOptionsProvider"/> is null,
  /// physical-field values are present (those require shadow-property hydration through
  /// EF's interceptor pipeline), or the caller didn't supply a non-empty table name.
  /// </remarks>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Single atomic UPSERT contains all the JSONB serialization + physical-field interpolation + retry-or-fallback control flow on purpose; splitting would force passing 6+ pieces of state across helpers and obscure the SQL composition.")]
  private static async Task<bool> _tryAtomicUpsertAsync<TModel>(
      DbContext context,
      UpsertRowArgs<TModel> args,
      PerEventApplyHookPlan hookPlan,
      DateTimeOffset? expiresAt,
      CancellationToken cancellationToken)
      where TModel : class {
    var optionsProvider = PathOnePersistenceOptionsProvider;
    if (optionsProvider is null) {
      return false;
    }
    if (string.IsNullOrEmpty(args.TableName)) {
      return false;
    }

    // The atomic upsert path runs raw Postgres-specific SQL via Npgsql commands —
    // it cannot execute against any other EF Core provider (InMemoryDatabase, Sqlite
    // test doubles, etc.). Bail out so the SELECT-then-UPDATE fallback path takes over
    // for those providers. Without this guard the path triggers whenever the
    // ModuleInitializer-set static PathOnePersistenceOptionsProvider is non-null,
    // making cross-test state leak into non-Postgres unit tests (Postgres-specific
    // perspective store wired against InMemoryDb fixtures fails on the JSONB raw SQL).
    if (!_isNpgsqlProvider(context)) {
      return false;
    }

    // Defense-in-depth identifier validation. args.TableName and the keys of
    // args.PhysicalFieldValues are NOT user input — they come from source-generated
    // perspective infrastructure and reflect compile-time table / column definitions.
    // But the atomic UPSERT path interpolates them into raw SQL, which Sonar S2077
    // flags as security-sensitive and could become a real exposure if the caller is
    // ever refactored. The regex matches the unquoted-identifier rule for PostgreSQL
    // (letter or underscore followed by letters, digits, or underscores). Reject
    // anything that doesn't pass — falls back to the SELECT-then-UPDATE retry path.
    if (!_isValidSqlIdentifier(args.TableName)) {
      return false;
    }
    if (args.PhysicalFieldValues?.Keys.Any(k => !_isValidSqlIdentifier(k)) == true) {
      return false;
    }

    var options = _resolvePersistenceOptions(optionsProvider);

    // Graceful fallback: the atomic path is an optimization. If the persistence union (+ user options)
    // can't resolve a type — e.g. an ad-hoc model not registered in any source-gen context — defer to the
    // legacy SELECT-then-INSERT path (which serializes through the DbContext's own, reflection-capable,
    // Npgsql JSON options) instead of throwing. We probe via the resolver, whose GetTypeInfo returns null
    // for an unresolvable type — unlike JsonSerializerOptions.GetTypeInfo, which throws NotSupportedException.
    var resolver = options.TypeInfoResolver;
    var modelInfo = resolver?.GetTypeInfo(typeof(TModel), options);
    var metadataInfo = resolver?.GetTypeInfo(typeof(PerspectiveMetadata), options);
    var scopeInfo = resolver?.GetTypeInfo(typeof(PerspectiveScope), options);
    if (modelInfo is null || metadataInfo is null || scopeInfo is null) {
      return false;
    }

    var dataJson = JsonSerializer.Serialize(args.Model, modelInfo);
    var metadataJson = JsonSerializer.Serialize(args.Metadata, metadataInfo);
    var scopeJson = JsonSerializer.Serialize(args.Scope, scopeInfo);

    // forceUpdateScope toggles whether scope participates in the DO UPDATE SET clause:
    // the INSERT path always sets scope so a new row carries the caller's scope verbatim,
    // while the UPDATE path preserves the existing row's scope unless the event is an
    // IScopeEvent.
    var scopeUpdateClause = args.ForceUpdateScope ? ", scope = EXCLUDED.scope" : string.Empty;

    // Physical-field columns (vector embeddings, denormalized scalars, etc.) ride
    // alongside the JSONB columns in the same atomic statement. Column names come from
    // EF model metadata via the caller's dictionary; we double-quote them to handle
    // mixed case + reserved-word collisions and bind values as ordinary parameters
    // (Vector + scalar types are handled natively by Npgsql once UseVector is on).
    var pfColumnsClause = string.Empty;
    var pfValuesClause = string.Empty;
    var pfUpdateClause = string.Empty;
    var pfCount = args.PhysicalFieldValues?.Count ?? 0;
    if (pfCount > 0) {
      var pfColumns = new StringBuilder();
      var pfValues = new StringBuilder();
      var pfUpdates = new StringBuilder();
      var i = 0;
      foreach (var columnName in args.PhysicalFieldValues!.Keys) {
        if (i > 0) {
          pfColumns.Append(", ");
          pfValues.Append(", ");
          pfUpdates.Append(", ");
        }
        // Double-quote the column to preserve case and dodge keyword collisions.
        var quoted = "\"" + columnName.Replace("\"", "\"\"") + "\"";
        pfColumns.Append(quoted);
        pfValues.Append("@pf_").Append(i);
        pfUpdates.Append(quoted).Append(" = EXCLUDED.").Append(quoted);
        i++;
      }
      pfColumnsClause = ", " + pfColumns;
      pfValuesClause = ", " + pfValues;
      pfUpdateClause = ", " + pfUpdates;
    }

    // Resolve schema from the EF Core model (set via HasDefaultSchema on the DbContext) and
    // schema-qualify the raw-SQL target. Without this, multi-tenant deployments that put
    // perspective tables in a service-specific schema ("bff", "inventory", ...) get
    // 42P01 "relation does not exist" because raw SQL doesn't honor EF's default schema —
    // the connection's search_path determines what unqualified names resolve to, and that
    // isn't set per-service in all deployments. PerspectiveRow<TModel> is the EF entity
    // registered for the perspective table; its schema comes from HasDefaultSchema().
    var entityType = context.Model.FindEntityType(typeof(PerspectiveRow<TModel>));
    var schema = entityType?.GetSchema();
    var qualifiedTable = string.IsNullOrEmpty(schema)
      ? args.TableName
      : $"\"{schema}\".{args.TableName}";

    // Opt-in via IVersionedApplyTarget: a model that implements the marker gets a stricter
    // WHERE clause that adds a UUIDv7 EventId tie-breaker on top of the legacy CommitSequence
    // comparison. The legacy permissive contract (null-CommitSeq lets the write through
    // unconditionally) remains the default — see
    // CrossPodStaleReadRegressionRaceTests.StaleSecondWriter_RegressesTerminalRowToEarlierState_StoreFailsToProtectAsync
    // — so existing consumers that depend on stamper-lag forwarding keep working.
    // Models that need strand prevention beyond the v0.740 stream-affinity gate (the first
    // is SagaItemModel) opt in by implementing IVersionedApplyTarget. A production saga
    // showed a small fraction of per-item rows survived the affinity gate and reverted to
    // State=Running — this marker closes that gap at the storage layer.
    var isVersionedTarget = typeof(IVersionedApplyTarget).IsAssignableFrom(typeof(TModel));
    var whereClause = isVersionedTarget
      ? $@"
        WHERE {qualifiedTable}.metadata->>'EventId' IS NULL
           OR EXCLUDED.metadata->>'EventId' > {qualifiedTable}.metadata->>'EventId'"
      : $@"
        WHERE {qualifiedTable}.metadata->>'CommitSequence' IS NULL
           OR EXCLUDED.metadata->>'CommitSequence' IS NULL
           OR (EXCLUDED.metadata->>'CommitSequence')::bigint >= ({qualifiedTable}.metadata->>'CommitSequence')::bigint";

    // updated_at + the version bump come from the resolved per-event hook plan (default whizbang.timestamps:
    // updated_at = now, bump = 1). created_at is always the real now on insert (a hook cannot rewrite it).
    // expires_at is TtlRow-only (E2-4d). For a non-TtlRow model (expiresAt == null) the clause is omitted
    // entirely, so the SQL is byte-identical to the pre-E2-4d form — a perspective table that never carries
    // the column (the common case + hand-written test schemas) is unaffected. A TtlRow perspective uses the
    // generated schema, which has the column.
    var expiresColumn = expiresAt.HasValue ? ", expires_at" : "";
    var expiresValue = expiresAt.HasValue ? ", @wb_expires" : "";
    var expiresUpdate = expiresAt.HasValue ? "\n          expires_at = EXCLUDED.expires_at," : "";
    var sql = $@"
        INSERT INTO {qualifiedTable} (id, data, metadata, scope, created_at, updated_at, version{expiresColumn}{pfColumnsClause})
        VALUES (@id, @data::jsonb, @metadata::jsonb, @scope::jsonb, @wb_created, @wb_updated, 1{expiresValue}{pfValuesClause})
        ON CONFLICT (id) DO UPDATE SET
          data = EXCLUDED.data,
          metadata = EXCLUDED.metadata,
          updated_at = EXCLUDED.updated_at,{expiresUpdate}
          version = {qualifiedTable}.version + @wb_versionbump{scopeUpdateClause}{pfUpdateClause}{whereClause}";

    var connection = context.Database.GetDbConnection();
    var openedHere = false;
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
      openedHere = true;
    }
    try {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = sql;
      // Honor an ambient EF transaction so the atomic upsert participates in it.
      var currentTx = context.Database.CurrentTransaction;
      if (currentTx is not null) {
        cmd.Transaction = currentTx.GetDbTransaction();
      }
      cmd.Parameters.Add(new NpgsqlParameter("id", args.Id));
      cmd.Parameters.Add(new NpgsqlParameter("data", dataJson));
      cmd.Parameters.Add(new NpgsqlParameter("metadata", metadataJson));
      cmd.Parameters.Add(new NpgsqlParameter("scope", scopeJson));
      cmd.Parameters.Add(new NpgsqlParameter("wb_created", DateTime.UtcNow));
      cmd.Parameters.Add(new NpgsqlParameter("wb_updated", hookPlan.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow));
      if (expiresAt.HasValue) {
        cmd.Parameters.Add(new NpgsqlParameter("wb_expires", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = expiresAt.Value.UtcDateTime });
      }
      cmd.Parameters.Add(new NpgsqlParameter("wb_versionbump", hookPlan.BumpVersion ? 1 : 0));
      if (pfCount > 0) {
        var i = 0;
        foreach (var value in args.PhysicalFieldValues!.Values) {
          // value is what the consumer's source generator already coerced to the
          // EF-mapped CLR type (Vector for embedding columns, string/decimal/etc.
          // for scalars). Null values bind as DBNull so nullable columns work.
          cmd.Parameters.Add(new NpgsqlParameter("pf_" + i, value ?? (object)DBNull.Value));
          i++;
        }
      }
      await cmd.ExecuteNonQueryAsync(cancellationToken);
      return true;
    } catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or System.Text.Json.JsonException) {
      // The atomic path couldn't serialize or bind this row — e.g. a CLR type Npgsql can't map to its
      // column (a WhizbangId physical-field value bound to a uuid column), or a model the persistence
      // union can serialize but Npgsql can't parameterize. Defer to the legacy SELECT-then-INSERT path,
      // whose EF value converters handle those CLR↔column mappings. INSERT … ON CONFLICT is a single
      // atomic statement, so a throw here means nothing was committed — falling back is safe (no
      // double-write). The atomic path stays a best-effort optimization.
      return false;
    } finally {
      if (openedHere && connection.State == System.Data.ConnectionState.Open) {
        await connection.CloseAsync();
      }
    }
  }

  /// <summary>
  /// Detects PostgreSQL unique-constraint violation (23505) inside a DbUpdateException.
  /// </summary>
  private static bool _isDuplicateKeyException(DbUpdateException ex) {
    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) {
      // Npgsql.PostgresException exposes SqlState; check via reflection-free duck typing
      if (inner is Npgsql.PostgresException pg && pg.SqlState == "23505") {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Defense-in-depth check for SQL identifiers (table + column names) interpolated into
  /// the atomic UPSERT raw SQL. PostgreSQL unquoted identifier rule: letter or underscore
  /// followed by letters, digits, or underscores. Hand-rolled (no regex) to avoid any
  /// regex-timeout concern and to keep the check zero-allocation on the hot path.
  /// </summary>
  /// <summary>
  /// Returns true when the DbContext is wired to the Npgsql provider. The atomic UPSERT
  /// path issues raw `INSERT … ON CONFLICT DO UPDATE` SQL that only PostgreSQL supports;
  /// other providers (InMemoryDatabase, Sqlite) must fall back to the SELECT-then-UPDATE
  /// retry path. The provider name comes from EF Core's `Database.ProviderName` and
  /// matches the assembly name of the active provider package.
  /// </summary>
  private static bool _isNpgsqlProvider(DbContext context) {
    return context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
  }

  private static bool _isValidSqlIdentifier(string s) {
    if (string.IsNullOrEmpty(s) || s.Length > 63) {
      return false; // PG identifier max is 63 bytes
    }
    var first = s[0];
    if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_')) {
      return false;
    }
    for (var i = 1; i < s.Length; i++) {
      var c = s[i];
      if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
        return false;
      }
    }
    return true;
  }

  private async Task _upsertCoreInnerAsync<TModel>(
      DbContext context,
      UpsertRowArgs<TModel> args,
      PerEventApplyHookPlan hookPlan,
      DateTimeOffset? expiresAt,
      CancellationToken cancellationToken)
      where TModel : class {
    var id = args.Id;
    var model = args.Model;
    var metadata = args.Metadata;
    var scope = args.Scope;
    var physicalFieldValues = args.PhysicalFieldValues;
    var forceUpdateScope = args.ForceUpdateScope;
    // Check if entity exists in local tracker and detach it to avoid tracking conflicts.
    // EF Core 10's ComplexProperty().ToJson() maintains internal indexes for collections
    // inside complex types. Any modification to tracked complex type collections corrupts
    // these indexes, causing ArgumentOutOfRangeException during AcceptChanges.
    // By detaching tracked entities and using AsNoTracking() for queries, we ensure
    // no corrupted tracking state exists when we attach the updated entity.
    var localRow = context.Set<PerspectiveRow<TModel>>().Local
        .FirstOrDefault(r => r.Id == id);

    if (localRow != null) {
      context.Entry(localRow).State = EntityState.Detached;
    }

    // Query database WITHOUT tracking to get a clean entity with no internal tracking state.
    var existingRow = await context.Set<PerspectiveRow<TModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Id)
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;
    // updated_at + the version bump come from the resolved per-event hook plan (default whizbang.timestamps:
    // updated_at = now, bump). A hook may override the stamp or skip the bump.
    var updatedAt = hookPlan.UpdatedAt?.UtcDateTime ?? now;

    PerspectiveRow<TModel> row;
    if (existingRow != null) {
      // Cross-pod lost-update guard: refuse a write whose commit_sequence is strictly below the
      // stored row's. A staler concurrent apply from a second instance (which loaded an empty/older
      // row and never saw the fresher write) must not overwrite a newer apply — this is what reverted
      // a per-item row from Completed to Running and stranded a production saga. A NULL on either
      // side can't be ordered, so it falls through to the stream-ownership guarantee in the claim path.
      if (existingRow.Metadata?.CommitSequence is long storedCommitSequence
          && metadata.CommitSequence is long incomingCommitSequence
          && incomingCommitSequence < storedCommitSequence) {
        if (ClearChangeTrackerAfterSave) {
          context.ChangeTracker.Clear();
        }
        return;
      }

      // Create updated row with new complex type instances.
      // We use Update() to attach as Modified, which will update all columns.
      row = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = forceUpdateScope ? CloneScope(scope) : CloneScope(existingRow.Scope),
        CreatedAt = existingRow.CreatedAt,
        UpdatedAt = updatedAt,
        Version = hookPlan.BumpVersion ? existingRow.Version + 1 : existingRow.Version
      };
      context.Set<PerspectiveRow<TModel>>().Update(row);
      if (!forceUpdateScope) {
        // SECURITY: Exclude scope from UPDATE SQL. Scope is set only on INSERT.
        // When forceUpdateScope is true (IScopeEvent), scope IS included in UPDATE.
        // Guard: only mark as unmodified when Scope is mapped as a complex property.
        var entityType = context.Entry(row).Metadata;
        if (entityType.FindComplexProperty(nameof(PerspectiveRow<TModel>.Scope)) != null) {
          context.Entry(row).ComplexProperty(e => e.Scope).IsModified = false;
        }
      }
    } else {
      row = _createNewRow(id, model, metadata, scope, now, updatedAt);
      context.Set<PerspectiveRow<TModel>>().Add(row);
    }

    // TtlRow perspective-row expiry (E2-4d): set the expires_at SHADOW property (a sliding last-activity
    // window). ONLY for TtlRow models (expiresAt.HasValue) so a context that never declares the shadow
    // property (every non-TtlRow / hand-configured test context) is untouched — no CLR property, no auto-map.
    if (expiresAt.HasValue) {
      context.Entry(row).Property("expires_at").CurrentValue = expiresAt.Value.UtcDateTime;
    }

    if (physicalFieldValues != null) {
      var entry = context.Entry(row);
      foreach (var (columnName, value) in physicalFieldValues) {
        // Values should already be the correct type (Vector, not float[])
        // The source generator converts float[] to Vector at compile time
        entry.Property(columnName).CurrentValue = value;
      }
    }

    await context.SaveChangesAsync(cancellationToken);

    if (ClearChangeTrackerAfterSave) {
      context.ChangeTracker.Clear();
    }
  }

  private static PerspectiveRow<TModel> _createNewRow<TModel>(
      Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope, DateTime createdAt, DateTime updatedAt)
      where TModel : class =>
    new() {
      Id = id,
      Data = model,
      Metadata = CloneMetadata(metadata),
      Scope = CloneScope(scope),
      CreatedAt = createdAt,
      UpdatedAt = updatedAt,
      Version = 1
    };

  /// <summary>
  /// Creates a clone of PerspectiveMetadata to avoid EF Core tracking issues.
  /// </summary>
  protected static PerspectiveMetadata CloneMetadata(PerspectiveMetadata metadata) {
    return new PerspectiveMetadata {
      EventType = metadata.EventType,
      EventId = metadata.EventId,
      Timestamp = metadata.Timestamp,
      CorrelationId = metadata.CorrelationId,
      CausationId = metadata.CausationId,
      CommitSequence = metadata.CommitSequence
    };
  }

  /// <summary>
  /// Creates a clone of PerspectiveScope to avoid EF Core tracking issues.
  /// </summary>
  protected static PerspectiveScope CloneScope(PerspectiveScope scope) {
    return new PerspectiveScope {
      TenantId = scope.TenantId,
      CustomerId = scope.CustomerId,
      UserId = scope.UserId,
      OrganizationId = scope.OrganizationId,
      AllowedPrincipals = [.. (scope.AllowedPrincipals ?? [])],
      Extensions = [.. (scope.Extensions ?? [])]
    };
  }

  /// <summary>
  /// Updates PerspectiveMetadata in place for tracked entities.
  /// Required for EF Core 10 ComplexProperty().ToJson() to avoid index corruption.
  /// </summary>
  /// <docs>data/efcore-complex-types#in-place-updates</docs>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs:UpdateMetadataInPlace_AllProperties_UpdatesTargetCorrectlyAsync</tests>
  protected static void UpdateMetadataInPlace(PerspectiveMetadata target, PerspectiveMetadata source) {
    target.EventType = source.EventType;
    target.EventId = source.EventId;
    target.Timestamp = source.Timestamp;
    target.CorrelationId = source.CorrelationId;
    target.CausationId = source.CausationId;
    target.CommitSequence = source.CommitSequence;
  }

  /// <summary>
  /// Updates PerspectiveScope in place for tracked entities.
  /// CRITICAL: Must clear and re-add collection items, NOT replace the List instances.
  /// EF Core 10's InternalComplexCollectionEntry maintains indexes into collections.
  /// Replacing List instances corrupts those indexes causing ArgumentOutOfRangeException.
  /// </summary>
  /// <docs>data/efcore-complex-types#in-place-updates</docs>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs:UpdateScopeInPlace_ScalarProperties_UpdatesTargetCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs:UpdateScopeInPlace_AllowedPrincipals_ClearsAndAddsNewItemsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs:UpdateScopeInPlace_AllowedPrincipals_EmptySource_ClearsTargetAsync</tests>
  protected static void UpdateScopeInPlace(PerspectiveScope target, PerspectiveScope source) {
    target.TenantId = source.TenantId;
    target.CustomerId = source.CustomerId;
    target.UserId = source.UserId;
    target.OrganizationId = source.OrganizationId;

    // Clear and re-add collection items - DO NOT replace the List instances
    target.AllowedPrincipals.Clear();
    target.AllowedPrincipals.AddRange(source.AllowedPrincipals);

    target.Extensions.Clear();
    foreach (var extension in source.Extensions) {
      target.Extensions.Add(new ScopeExtension(extension.Key, extension.Value));
    }
  }
}
