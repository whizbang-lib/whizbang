using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Singleton adapter that lets the dispatch / perspective workers inject
/// <see cref="IDeadLetterStore"/> while the underlying <see cref="EFCoreDeadLetterStore{TDbContext}"/>
/// needs the consumer's scoped <c>DbContext</c>. Opens a fresh DI scope per
/// <c>MoveAsync</c> call so the work happens against a per-call EF Core connection
/// rather than a process-wide one.
/// </summary>
/// <remarks>
/// Resolves the consumer's DbContext via the captured runtime type (registered by
/// <see cref="PostgresDriverExtensions"/>). Downcasts to base <see cref="DbContext"/>
/// for the inner store — the EFCore impl only calls base <c>Database.GetDbConnection()</c>
/// methods, so the concrete type doesn't matter at the storage layer.
/// </remarks>
internal sealed class ScopedEFCoreDeadLetterStore : IDeadLetterStore {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;
  private readonly ILogger<EFCoreDeadLetterStore<DbContext>> _logger;
  private readonly WorkCoordinatorGate? _gate;

  public ScopedEFCoreDeadLetterStore(
      IServiceScopeFactory scopeFactory,
      Type dbContextType,
      ILogger<EFCoreDeadLetterStore<DbContext>> logger,
      WorkCoordinatorGate? gate) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _gate = gate;
  }

  public async Task<Guid?> MoveAsync(
      Guid deadLetterId,
      string sourceTable,
      Guid sourceId,
      MessageFailureReason failureReason,
      string? errorText,
      Guid instanceId,
      string generation,
      CancellationToken ct = default) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
    var inner = new EFCoreDeadLetterStore<DbContext>(dbContext, _logger, _gate);
    return await inner.MoveAsync(
      deadLetterId, sourceTable, sourceId, failureReason, errorText, instanceId, generation, ct)
      .ConfigureAwait(false);
  }
}
