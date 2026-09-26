namespace Whizbang.Sagas.Models;

/// <summary>
/// A saga the stranded-saga sweep should consider, and the tenant it belongs to.
/// </summary>
/// <remarks>
/// The tenant travels with the saga because the sweep runs on a maintenance worker with no request
/// of its own. A watchdog tick it arms must be handled in the saga's tenant, exactly as the tick the
/// saga armed for itself was; otherwise the recovery reads and writes in no tenant at all.
/// <see langword="null"/> when sagas are not tenant-scoped, and the tick is then published for all
/// tenants.
/// </remarks>
/// <param name="Saga">The saga as its projection records it.</param>
/// <param name="TenantId">The saga's tenant, or <see langword="null"/> when sagas are not tenant-scoped.</param>
/// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs</tests>
public sealed record IncompleteSaga(BaseSagaModel Saga, string? TenantId);
