using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #697: the perspective registry key is the CLR type name (<c>Outer+Model</c> for a nested
/// model), written by the generator through <c>TypeNameUtilities.BuildClrTypeName</c> and looked up
/// by the runtime through <c>TypeNameFormatter.FormatClrTypeName</c>. Before, the generator wrote a
/// display-string rendering (<c>Outer.Model</c>): <c>sync_perspective_retention</c> matched zero
/// rows for every nested model, the result was discarded, and row retention was silently
/// un-enrolled. The existing enforcement tests used a top-level model, which is why it never showed.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Shard3")]
public class PerspectiveRegistryKeyTests : EFCoreTestBase {
  private static readonly string _nestedKey = TypeNameFormatter.FormatClrTypeName(typeof(NestedRetentionOwner.Model));

  [Test]
  public async Task NestedModel_RegistryRowIsKeyedByTheClrFormAsync() {
    // The base setup ran the generated initializer, which reconciled the registry from the
    // generator's JSON: the real path from the model type to the row.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    var keys = await _registryKeysLikeAsync(conn, "%NestedRetentionOwner%");

    await Assert.That(keys).Count().IsEqualTo(1);
    await Assert.That(keys[0]).IsEqualTo(_nestedKey)
      .Because("the registry key is the CLR form the runtime looks up, with '+' for the nesting");
    await Assert.That(keys[0]).Contains("+");
  }

  [Test]
  public async Task DeclaredTtl_OnNestedModel_ReachesTheRegistryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await coordinator.SyncPerspectiveRetentionAsync([
      new PerspectiveRetentionDeclaration(_nestedKey, Enrolled: true, TtlSeconds: 86400, MaxAgeSeconds: null)
    ]);

    var (enrolled, ttl) = await _retentionAsync(conn, _nestedKey);
    await Assert.That(enrolled).IsTrue().Because("the declaration keyed by the CLR form matches the generator's row");
    await Assert.That(ttl).IsEqualTo(86400);
  }

  [Test]
  public async Task Reconciler_NestedModel_DeclaresTheClrKeyAsync() {
    // The generated module initializer registered the TTL; register again so a test elsewhere
    // that reconfigures the static registry cannot hide the model.
    PerspectiveTtlRegistry.Register(typeof(NestedRetentionOwner.Model), 86400);
    var coordinator = new CapturingCoordinator();
    var reconciler = new TypeDefinitionReconciler(
      new SingleCoordinatorScopeFactory(coordinator),
      Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = false }),
      NullLogger<TypeDefinitionReconciler>.Instance,
      new EmptyCatalog());

    await reconciler.ReconcileAsync();

    var declaration = coordinator.Declarations.SingleOrDefault(d => d.ClrTypeName.Contains("NestedRetentionOwner", StringComparison.Ordinal));
    await Assert.That(declaration).IsNotNull();
    await Assert.That(declaration!.ClrTypeName).IsEqualTo(_nestedKey)
      .Because("the declaration key comes from TypeNameFormatter.FormatClrTypeName, the runtime mirror of the generator's helper");
  }

  [Test]
  public async Task Sync_DeclarationMatchingNoRegistryRow_LogsAWarningNamingTheTypeAsync() {
    await using var ctx = CreateDbContext();
    var logger = new CapturingLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), logger);

    await coordinator.SyncPerspectiveRetentionAsync([
      new PerspectiveRetentionDeclaration("TestApp.NoSuchOwner+Model", Enrolled: true, TtlSeconds: 60, MaxAgeSeconds: null)
    ]);

    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings).Count().IsEqualTo(1)
      .Because("a declaration that matches no registry row is a key drift, never a swallowed zero");
    await Assert.That(warnings[0].Message).Contains("TestApp.NoSuchOwner+Model");
  }

  // -------------------------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<List<string>> _registryKeysLikeAsync(NpgsqlConnection conn, string pattern) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT clr_type_name FROM wh_perspective_registry WHERE clr_type_name LIKE @p ORDER BY clr_type_name";
    cmd.Parameters.AddWithValue("p", pattern);
    var keys = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      keys.Add(reader.GetString(0));
    }
    return keys;
  }

  private static async Task<(bool Enrolled, int? TtlSeconds)> _retentionAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT row_retention_enrolled, row_ttl_seconds FROM wh_perspective_registry WHERE clr_type_name = @k";
    cmd.Parameters.AddWithValue("k", key);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (false, null);
    }
    return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
  }

  private sealed class EmptyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
  }

  private sealed class SingleCoordinatorScopeFactory(IWorkCoordinator coordinator)
    : IServiceScopeFactory, IServiceScope, IServiceProvider {
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) => serviceType == typeof(IWorkCoordinator) ? coordinator : null;
    public void Dispose() { }
  }

  /// <summary>Captures the retention declarations; every other member is inert.</summary>
  private sealed class CapturingCoordinator : IWorkCoordinator {
    public List<PerspectiveRetentionDeclaration> Declarations { get; } = [];

    public Task SyncPerspectiveRetentionAsync(IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
      Declarations.AddRange(declarations);
      return Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<StreamEventData>());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }
}
