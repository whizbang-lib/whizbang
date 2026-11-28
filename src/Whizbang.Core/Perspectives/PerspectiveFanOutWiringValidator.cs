// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Refuses at startup a host that declares a perspective fan-out it cannot apply (#2620): one with no collective
/// dispatcher, or with a fan-out model the dispatcher can have no executor for. The message names the models and the
/// line to add, so a fan-out is never declared and silently never applied.
/// </summary>
/// <remarks>
/// Registered by every assembly's fan-out registration
/// (<see cref="PerspectiveFanOutRegistryServiceCollectionExtensions.AddPerspectiveFanOutRegistryContribution{TRegistry}"/>),
/// so a host that declares a fan-out is checked whatever else it registers. The EF Core Postgres driver registers the
/// dispatcher for such a host, and the dispatcher makes each fan-out model's executor
/// (<see cref="CollectiveExecutors.ForDispatch"/>), so a host built with the driver passes with nothing added. It reads
/// only registrations and does no database work.
/// </remarks>
/// <param name="services">The host's services.</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/FanOutWiringTests.cs</tests>
public sealed class PerspectiveFanOutWiringValidator(IServiceProvider services) : IHostedService {
  private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/FanOutWiringTests.cs:Validator_AFanOutWithNoCollectiveDispatcher_RefusesTheHost_NamingTheModelAndTheLineAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/FanOutWiringTests.cs:Validator_AFanOutModelNoExecutorCanBeMadeFor_RefusesTheHost_NamingTheLineAsync</tests>
  public Task StartAsync(CancellationToken cancellationToken) {
    var fanOuts = _services.GetPerspectiveFanOutEntries();
    if (fanOuts.Count == 0) {
      return Task.CompletedTask;
    }
    if (_services.GetService<ICollectiveDispatcher>() is null) {
      throw new InvalidOperationException(
        $"Perspectives fan out to {_models(fanOuts)}, but no collective dispatcher is registered, so no fan-out would ever "
        + "be applied. The EF Core Postgres driver (.WithEFCore<TDbContext>().WithDriver.Postgres) registers it; otherwise add "
        + "services.AddCollectiveEventsEFCore<TDbContext>() or services.AddCollectiveEventsDapper().");
    }
    var executed = new HashSet<Type>(_services.GetServices<ICollectiveEventExecutor>().Select(e => e.ModelType));
    var unexecutable = fanOuts.Where(f => f.CreateExecutor is null && !executed.Contains(f.ModelType)).ToList();
    if (unexecutable.Count > 0) {
      throw new InvalidOperationException(
        $"Perspectives fan out to {_models(unexecutable)}, which no collective executor applies: these fan-outs were not "
        + "registered by the generator, so the executor cannot be made for them. Add "
        + string.Join(" and ", unexecutable.Select(f => f.ModelType).Distinct()
          .Select(m => $"services.AddCollectiveExecutorEFCore<{TypeNameFormatter.ForLog(m)}>()"))
        + " (or AddCollectiveExecutorDapper<TModel>(table)).");
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  private static string _models(IEnumerable<PerspectiveFanOutEntry> fanOuts) =>
    string.Join(", ", fanOuts.Select(f => TypeNameFormatter.ForLog(f.ModelType)).Distinct(StringComparer.Ordinal));
}
