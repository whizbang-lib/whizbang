using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace ECommerce.Integration.TestUtilities.Fixtures;

/// <summary>
/// Centralizes the test-specific tightening of Whizbang's worker-timing options
/// (backup-tick coordinator + sliding-window batchers) so every integration
/// fixture in the ECommerce sample suite shares a single canonical setup.
/// </summary>
/// <remarks>
/// <para>
/// Production defaults bake in long polling intervals (BackupTickCoordinator
/// IdleThreshold + PollingInterval at 30 s; sliding-window MaxWait 1–3 s) so
/// idle services don't burn DB/CPU on no-op polls. End-to-end integration
/// tests fire one event per hop through a 10-step message chain; every hop
/// inheriting those production defaults compounds into 30+ s per-hop waits
/// that exhaust the test's CT-driven deadline.
/// </para>
/// <para>
/// PR #251 forensic (Jun 2026) traced
/// <c>PerspectiveStages_MultipleEvents_AllStagesFireForEachAsync</c> to this
/// exact compounding. Test runs alternated between 7 s (lucky NOTIFY timing)
/// and 70+ s (sliding-window MaxWaits compounded across hops) on the same
/// branch and fixture — the variance bit CI under parallel-test pressure and
/// surfaced as <c>TaskCanceledException</c> inside
/// <c>LifecycleStageTestExtensions._waitForLifecycleStageAsync</c>. Tightening
/// these knobs to sub-second values reduces the variance to noise.
/// </para>
/// <para>
/// Call <see cref="ApplyTestTimings"/> once per host (Inventory + BFF) inside
/// each fixture's host configuration.
/// </para>
/// </remarks>
public static class TestWorkerTimingOverrides {
  /// <summary>
  /// Applies the canonical test-side timing overrides to <paramref name="services"/>.
  /// Safe to call after <c>AddWhizbang</c> / <c>AddWhizbangWorkers</c> — the
  /// <c>Configure&lt;T&gt;</c> callbacks layer onto the default options, and the
  /// explicit <c>IOptions&lt;T&gt;</c> registrations for the init-only
  /// <c>SlidingWindow*Options</c> override their prior registrations
  /// (last AddSingleton wins).
  /// </summary>
  public static IServiceCollection ApplyTestTimings(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // ClaimWorker — tighten NOTIFY-healthy poll cadence so a missed NOTIFY only
    // costs 500 ms (not the 30 s production default).
    services.Configure<ClaimWorkerOptions>(options => {
      options.NotifyHealthyPollingIntervalMilliseconds = 500;
    });

    // BackupTickCoordinator — tighten idle-wake cadence. IdleThreshold short so
    // workers exit ASLEEP quickly when test traffic arrives; PollingInterval
    // short so backup-tick checks happen frequently even when NOTIFY misses.
    services.Configure<BackupTickCoordinatorOptions>(options => {
      options.IdleThreshold = TimeSpan.FromSeconds(1);
      options.PollingInterval = TimeSpan.FromMilliseconds(500);
    });

    // Sliding-window batchers — drop MaxWait to 100 ms so single-event hops in
    // tests don't sit waiting for a fill that will never come. Init-only
    // properties on these options mean Configure<T>(callback) can't mutate
    // them; explicit IOptions<T> registrations replace the AddOptions<T>()
    // defaults via last-wins resolution.
    services.AddSingleton<IOptions<SlidingWindowBatcherOptions>>(
      Options.Create(new SlidingWindowBatcherOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(100),
      }));
    services.AddSingleton<IOptions<SlidingWindowOutboxOptions>>(
      Options.Create(new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(100),
      }));
    services.AddSingleton<IOptions<SlidingWindowInboxOptions>>(
      Options.Create(new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(100),
      }));
    services.AddSingleton<IOptions<SlidingWindowApplyOptions>>(
      Options.Create(new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(100),
      }));

    return services;
  }
}
