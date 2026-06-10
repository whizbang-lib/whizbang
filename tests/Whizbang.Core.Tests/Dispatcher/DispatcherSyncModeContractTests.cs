#pragma warning disable CA1707

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Locks the W4 dispatcher API cleanup surface:
///   - <see cref="SyncMode"/> enum exists with both <c>StreamOnly</c> + <c>AllProjections</c>
///   - The new <c>LocalInvokeAndSyncAsync(message, mode, ct)</c> method has the right shape
///     (no <c>TimeSpan</c> parameter, default mode is <see cref="SyncMode.AllProjections"/>)
///   - Old timeout-shaped overloads are marked <see cref="System.ObsoleteAttribute"/>.
/// </summary>
/// <remarks>
/// These are reflection-based contract tests — they don't exercise behavior, they pin the
/// shape so a future refactor can't silently revert. Behavioral tests for the new method live
/// in <c>DispatcherSyncModeBehaviorTests</c> (Slice 4 of the original plan; deferred since
/// Phase 0 proved the framework already waits correctly).
/// </remarks>
/// <docs>fundamentals/dispatcher/sync-mode</docs>
public class DispatcherSyncModeContractTests {

  [Test]
  public async Task SyncMode_EnumExists_WithStreamOnlyAndAllProjectionsAsync() {
    // Pin the enum surface so adding/removing values is a breaking-change signal.
    var values = Enum.GetValues<SyncMode>();
    await Assert.That(values).Contains(SyncMode.StreamOnly)
      .Because("StreamOnly is the opt-in fast path for callers that don't read after writing.");
    await Assert.That(values).Contains(SyncMode.AllProjections)
      .Because("AllProjections is the read-after-write CQRS default.");
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_NewOverload_SyncModeIsRequiredNotDefaultedAsync() {
    // Design lock: the SyncMode parameter MUST NOT have a default value. Two reasons:
    //   1. C# overload resolution: a defaulted SyncMode + the legacy timeout-shaped overload's
    //      defaulted TimeSpan? would make `LocalInvokeAndSyncAsync(cmd)` ambiguous (matches
    //      both). Requiring SyncMode at the callsite resolves it.
    //   2. Design intent: read-after-write vs. fast-path is a meaningful per-call decision.
    //      Making it explicit at the callsite surfaces intent — and prevents a future
    //      refactor from silently flipping a default that callers depend on.
    var method = typeof(IDispatcher).GetMethods()
      .Where(m => m.Name == "LocalInvokeAndSyncAsync")
      .Where(m => m.GetParameters().Length == 3)
      .Where(m => m.GetParameters()[1].ParameterType == typeof(SyncMode))
      .SingleOrDefault();

    await Assert.That(method).IsNotNull()
      .Because("The W4 LocalInvokeAndSyncAsync<TMessage>(message, SyncMode, CancellationToken) overload MUST exist on IDispatcher.");

    var modeParam = method!.GetParameters()[1];
    await Assert.That(modeParam.HasDefaultValue).IsFalse()
      .Because("SyncMode is required (no default) so the read-after-write expectation is explicit at every callsite — and to disambiguate from the legacy timeout overload.");
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_NewOverload_HasNoTimeSpanParameterAsync() {
    // The W4 cleanup's core complaint: the timeout-shaped API encourages timing-based defenses
    // where signal-based should be enough. The new method MUST be CT-only.
    var method = typeof(IDispatcher).GetMethods()
      .Where(m => m.Name == "LocalInvokeAndSyncAsync")
      .Where(m => m.GetParameters().Length == 3)
      .Where(m => m.GetParameters()[1].ParameterType == typeof(SyncMode))
      .Single();

    var hasTimeSpan = method.GetParameters().Any(p => p.ParameterType == typeof(TimeSpan?) || p.ParameterType == typeof(TimeSpan));
    await Assert.That(hasTimeSpan).IsFalse()
      .Because("The new method MUST NOT take a TimeSpan parameter — CancellationToken is the only wait bound. Adding back a timeout would defeat the W4 cleanup's purpose.");

    var hasCt = method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken));
    await Assert.That(hasCt).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_OldOverloads_AreMarkedObsoleteAsync() {
    // Pin that the timeout-shaped overloads continue to carry [Obsolete] so consumers
    // migrating to the new shape see compiler warnings pointing at it.
    var oldOverloads = typeof(IDispatcher).GetMethods()
      .Where(m => m.Name == "LocalInvokeAndSyncAsync")
      .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(TimeSpan?)))
      .ToList();

    await Assert.That(oldOverloads.Count).IsGreaterThanOrEqualTo(3)
      .Because("There are three timeout-based overloads (TMessage, TMessage+TResult, TMessage+TResult+TPerspective). All MUST be marked [Obsolete].");

    foreach (var overload in oldOverloads) {
      var attr = overload.GetCustomAttribute<ObsoleteAttribute>();
      await Assert.That(attr).IsNotNull()
        .Because($"Old overload {overload} MUST carry [Obsolete] — without it, callers have no compiler-visible signal to migrate to the new SyncMode shape.");
      await Assert.That(attr!.Message).IsNotNull();
      await Assert.That(attr.Message!).Contains("SyncMode")
        .Because("The [Obsolete] message MUST point at the SyncMode replacement so consumers know where to migrate.");
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_NewOverload_ReturnsValueTaskAsync() {
    // ValueTask matches the surrounding LocalInvokeAsync overloads (hot-path zero-allocation
    // target). The old timeout-based methods return Task<SyncResult> — that's part of the
    // legacy surface we're moving away from. Locks the new shape.
    var method = typeof(IDispatcher).GetMethods()
      .Where(m => m.Name == "LocalInvokeAndSyncAsync")
      .Where(m => m.GetParameters().Length == 3)
      .Where(m => m.GetParameters()[1].ParameterType == typeof(SyncMode))
      .Single();

    await Assert.That(method.ReturnType).IsEqualTo(typeof(ValueTask))
      .Because("The W4 method returns ValueTask (no SyncResult struct — perspective health is observable elsewhere). Consistent with LocalInvokeAsync's ValueTask return.");
  }
}
