using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Coverage for two <see cref="SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync"/>
/// branches never exercised elsewhere: the legacy <c>timeoutSeconds &lt;= 0</c> path that awaits
/// the provider directly (every worker-level timeout test configures a positive timeout), and the
/// abandoned-task continuation that observes a LATE failure — a provider that only faults after
/// the caller has already moved on with <c>TimedOut</c>. That continuation exists specifically so a
/// late exception is observed rather than surfacing as an unobserved task exception at GC; if it
/// didn't run, a caller who already handled the timeout would later see a spurious crash report for
/// a failure it already dealt with.
/// </summary>
public class SecurityContextHelperCoverageTests {

  private sealed class _immediateProvider : IMessageSecurityContextProvider {
    public ValueTask<IScopeContext?> EstablishContextAsync(IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<IScopeContext?>(null);
  }

  /// <summary>Blocks on a caller-controlled gate, then throws — simulating a provider that
  /// outlives the caller's timeout and only fails afterward. The failure message carries a unique
  /// marker so a parallel run's unrelated unobserved exceptions can't produce a false positive.</summary>
  private sealed class _lateFaultingProvider(TaskCompletionSource gate, string marker) : IMessageSecurityContextProvider {
    public bool Faulted { get; private set; }

    public async ValueTask<IScopeContext?> EstablishContextAsync(IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) {
      await gate.Task.ConfigureAwait(false);
      Faulted = true;
      throw new InvalidOperationException($"late failure after the caller's timeout already returned ({marker})");
    }
  }

  private static MessageEnvelope<object> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new object(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static ServiceProvider _providerWith(IMessageSecurityContextProvider security) =>
    new ServiceCollection().AddSingleton(security).BuildServiceProvider();

  /// <summary>What breaks: <c>timeoutSeconds &lt;= 0</c> is the documented "disable the timeout,
  /// restore legacy behavior" escape hatch. If it silently timed out instead of awaiting the
  /// provider directly, every caller of the legacy fast path would start seeing spurious
  /// TimedOut results for a provider that was going to succeed given the time.</summary>
  [Test]
  public async Task TryEstablishFullContextWithTimeoutAsync_TimeoutDisabled_AwaitsProviderDirectlyAsync() {
    var scopedProvider = _providerWith(new _immediateProvider());

    var outcome = await SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync(
      _envelope(), scopedProvider, timeoutSeconds: 0, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(SecurityContextEstablishmentOutcome.Success)
      .Because("timeoutSeconds <= 0 disables the timeout — the legacy behavior awaits the provider directly with no WaitAsync wrapper");
  }

  /// <summary>What breaks: the abandoned task's late fault must be observed by the pump's own
  /// continuation so it never surfaces as an unobserved task exception at GC — an operator relying
  /// on shutdown diagnostics must never see a spurious crash report for a timeout the caller
  /// already handled.</summary>
  [Test]
  [Timeout(30000)]
  public async Task TryEstablishFullContextWithTimeoutAsync_LateFailureAfterTimeout_IsObservedNotUnobservedAsync(CancellationToken testToken) {
    var marker = $"security-ctx-late-fault-{Guid.NewGuid():N}";
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new _lateFaultingProvider(gate, marker);
    var scopedProvider = _providerWith(provider);

    // Matched by the unique marker rather than "any unobserved exception fired" so a parallel
    // run's unrelated unobserved exceptions can never produce a false positive here.
    var sawOurMarkerUnobserved = false;
    void onUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception.InnerExceptions.Any(ex => ex.Message.Contains(marker, StringComparison.Ordinal))) {
        sawOurMarkerUnobserved = true;
      }
      e.SetObserved();
    }
    TaskScheduler.UnobservedTaskException += onUnobserved;
    try {
      var outcome = await SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync(
        _envelope(), scopedProvider, timeoutSeconds: 1, testToken);

      await Assert.That(outcome).IsEqualTo(SecurityContextEstablishmentOutcome.TimedOut)
        .Because("a provider that outlives the timeout must report TimedOut rather than blocking the caller forever");

      // Let the abandoned establishment fail now that the caller has already moved on.
      gate.SetResult();

      var deadline = DateTime.UtcNow.AddSeconds(10);
      while (DateTime.UtcNow < deadline && !provider.Faulted) {
        await Task.Delay(20, testToken);
      }
      await Assert.That(provider.Faulted).IsTrue()
        .Because("the test setup itself requires the abandoned task to actually fault for this to prove anything");

      // Force finalization repeatedly so an UNOBSERVED exception would have surfaced by now.
      var finalizeDeadline = DateTime.UtcNow.AddSeconds(5);
      while (DateTime.UtcNow < finalizeDeadline) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(20, testToken);
      }
    } finally {
      TaskScheduler.UnobservedTaskException -= onUnobserved;
    }

    await Assert.That(sawOurMarkerUnobserved).IsFalse()
      .Because("the abandoned continuation must observe the late exception itself, so it never surfaces as an unobserved task exception at GC");
  }
}
