using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A collective event + self-referential apply over <see cref="RebuildBalanceModel"/>, used by the rebuild
/// integration test to prove a collective mutation survives a perspective rebuild. The <c>[CollectiveApplyFor]</c>
/// attribute makes the source generator emit a <c>CollectiveApplyRegistry</c> entry for this (event, model),
/// which the replay applier reads.
/// </summary>
/// <remarks>
/// Self-referential (per WHIZ106): the setter is a constant and the <c>Where</c> keys off the row's own id, so
/// it re-applies deterministically to a single in-memory row during replay.
/// </remarks>
public record RebuildResetBalanceCollectiveEvent : CollectiveEventBase {
  /// <summary>The one row (by id) to reset.</summary>
  public required Guid TargetId { get; init; }

  /// <summary>The value to set <see cref="RebuildBalanceModel.Balance"/> to.</summary>
  public required decimal ResetTo { get; init; }
}

/// <summary>Concrete spec (mirrors the driver's expression-tree contract).</summary>
public sealed record RebuildBalanceSpec(
    Expression<Action<ICollectiveSetters<RebuildBalanceModel>>> Setters,
    Expression<Func<PerspectiveRow<RebuildBalanceModel>, bool>>? Where = null)
    : ICollectiveSpec<RebuildBalanceModel>;

/// <summary>Handler discovered by the generator; produces the reset spec for one row.</summary>
public sealed class RebuildBalanceCollectiveHandler {
  /// <summary>Reset the target row's balance — a constant setter gated by the row's own id.</summary>
  [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
  public ICollectiveSpec<RebuildBalanceModel> Reset(
      RebuildResetBalanceCollectiveEvent e, ICollectiveQuery query) =>
    new RebuildBalanceSpec(
      Setters: s => s.SetProperty(m => m.Balance, e.ResetTo),
      Where: r => r.Id == e.TargetId);
}
