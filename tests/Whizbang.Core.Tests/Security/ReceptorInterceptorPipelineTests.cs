using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="ReceptorInterceptorPipeline"/>. Covers first-denial vs.
/// run-all-and-pick-strictest semantics, empty-pipeline behavior, and ordering.
/// </summary>
/// <tests>ReceptorInterceptorPipeline</tests>
public class ReceptorInterceptorPipelineTests {
  private sealed class StubReceptor { }

  private sealed class AllowingInterceptor : IReceptorInterceptor {
    public int CallCount { get; private set; }
    public ValueTask<InterceptorResult> CanInvokeAsync(Type t, IMessageEnvelope e, IScopeContext? c, CancellationToken ct = default) {
      CallCount++;
      return new ValueTask<InterceptorResult>(InterceptorResult.Allowed);
    }
  }

  private sealed class DenyingInterceptor(DeniedAction action) : IReceptorInterceptor {
    public int CallCount { get; private set; }
    public ValueTask<InterceptorResult> CanInvokeAsync(Type t, IMessageEnvelope e, IScopeContext? c, CancellationToken ct = default) {
      CallCount++;
      return new ValueTask<InterceptorResult>(InterceptorResult.Deny(action));
    }
  }

  // ===== Empty pipeline =====

  [Test]
  public async Task EmptyPipeline_AlwaysAllowsAsync() {
    var p = new ReceptorInterceptorPipeline([]);
    var result = await p.EvaluateFirstDenialAsync(typeof(StubReceptor), null!, null);
    var allow = result.Allow;
    await Assert.That(allow).IsTrue();
  }

  // ===== EvaluateFirstDenial =====

  [Test]
  public async Task EvaluateFirstDenial_AllAllow_ReturnsAllowedAsync() {
    var a = new AllowingInterceptor();
    var b = new AllowingInterceptor();
    var p = new ReceptorInterceptorPipeline([a, b]);
    var result = await p.EvaluateFirstDenialAsync(typeof(StubReceptor), null!, null);
    var allow = result.Allow;
    var aCalls = a.CallCount;
    var bCalls = b.CallCount;
    await Assert.That(allow).IsTrue();
    await Assert.That(aCalls).IsEqualTo(1);
    await Assert.That(bCalls).IsEqualTo(1);
  }

  [Test]
  public async Task EvaluateFirstDenial_FirstDenies_ShortCircuitsAsync() {
    var a = new DenyingInterceptor(DeniedAction.DeadLetter);
    var b = new AllowingInterceptor();
    var p = new ReceptorInterceptorPipeline([a, b]);
    var result = await p.EvaluateFirstDenialAsync(typeof(StubReceptor), null!, null);
    var allow = result.Allow;
    var bCalls = b.CallCount;
    await Assert.That(allow).IsFalse();
    await Assert.That(bCalls).IsEqualTo(0);
  }

  [Test]
  public async Task EvaluateFirstDenial_ReturnsFirstDeniedActionAsync() {
    var a = new AllowingInterceptor();
    var b = new DenyingInterceptor(DeniedAction.Quarantine);
    var c = new DenyingInterceptor(DeniedAction.DeadLetter);
    var p = new ReceptorInterceptorPipeline([a, b, c]);
    var result = await p.EvaluateFirstDenialAsync(typeof(StubReceptor), null!, null);
    var action = result.OnDenied;
    var cCalls = c.CallCount;
    await Assert.That(action).IsEqualTo(DeniedAction.Quarantine);
    await Assert.That(cCalls).IsEqualTo(0);
  }

  // ===== EvaluateAll =====

  [Test]
  public async Task EvaluateAll_AllAllow_ReturnsAllowedAsync() {
    var p = new ReceptorInterceptorPipeline([new AllowingInterceptor(), new AllowingInterceptor()]);
    var result = await p.EvaluateAllAsync(typeof(StubReceptor), null!, null);
    var allow = result.Allow;
    await Assert.That(allow).IsTrue();
  }

  [Test]
  public async Task EvaluateAll_AllInterceptorsRun_EvenWhenFirstDeniesAsync() {
    var a = new DenyingInterceptor(DeniedAction.DropQuiet);
    var b = new DenyingInterceptor(DeniedAction.DeadLetter);
    var p = new ReceptorInterceptorPipeline([a, b]);
    _ = await p.EvaluateAllAsync(typeof(StubReceptor), null!, null);
    var aCalls = a.CallCount;
    var bCalls = b.CallCount;
    await Assert.That(aCalls).IsEqualTo(1);
    await Assert.That(bCalls).IsEqualTo(1);
  }

  [Test]
  public async Task EvaluateAll_PicksStrictestActionAsync() {
    // DropQuiet from one + DeadLetter from another — DeadLetter (strictest) must win
    // so a peer interceptor that wants to drop quiet doesn't downgrade an audit-required denial.
    var p = new ReceptorInterceptorPipeline([
      new DenyingInterceptor(DeniedAction.DropQuiet),
      new DenyingInterceptor(DeniedAction.DeadLetter),
      new DenyingInterceptor(DeniedAction.Quarantine),
    ]);
    var result = await p.EvaluateAllAsync(typeof(StubReceptor), null!, null);
    var action = result.OnDenied;
    await Assert.That(action).IsEqualTo(DeniedAction.DeadLetter);
  }
}
