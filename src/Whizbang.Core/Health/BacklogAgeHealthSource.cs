using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Health;

/// <summary>
/// Surfaces the backlog-age duty's findings on the managed-health component <c>backlog</c>
/// (topology arc phase 10). Degraded — never Faulted — while any entity's oldest message is older
/// than the configured threshold, with the ENTITY NAMED in the detail.
/// </summary>
/// <remarks>
/// <para>
/// The signal is advisory by construction: an aged backlog means a consumer is not keeping up, not
/// that this process is broken, so failing liveness on it would restart the wrong thing. What it
/// replaces is worse than a wrong alarm — it replaces silence. The failure mode this arc exists
/// for was invisible while every component reported healthy.
/// </para>
/// <para>
/// The source itself does no work: the periodic duty owns the peek, this only projects
/// <see cref="BacklogAgeState"/>.
/// </para>
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs:PeekOnce_AgedBacklog_DegradesHealthNamingTheEntityAsync</tests>
public sealed class BacklogAgeHealthSource : IWhizbangHealthSource {
  private readonly BacklogAgeState _state;

  /// <summary>Creates the source over the duty's shared state.</summary>
  /// <param name="state">The backlog-age state.</param>
  /// <exception cref="ArgumentNullException">Thrown when state is null.</exception>
  public BacklogAgeHealthSource(BacklogAgeState state) {
    ArgumentNullException.ThrowIfNull(state);
    _state = state;
  }

  /// <inheritdoc />
  public string Component => "backlog";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var aged = _state.AgedBacklogs;
    if (aged.Count == 0) {
      return ValueTask.FromResult(new ComponentHealth(ComponentState.Operational));
    }

    var detail = string.Join("; ", aged.Select(f => string.Create(
      CultureInfo.InvariantCulture,
      $"{f.Entity} ({f.TrafficClass} class, namespace '{f.TransportNamespace}') holds {f.Depth} "
      + $"message(s), oldest {f.OldestAge.TotalMinutes:F0} minute(s) old")));

    return ValueTask.FromResult(new ComponentHealth(
      ComponentState.Degraded,
      $"backlog-age duty: {detail} — a backlog this old is a consumer that is not draining, not a "
      + "burst; depth alone cannot tell those apart"));
  }
}
