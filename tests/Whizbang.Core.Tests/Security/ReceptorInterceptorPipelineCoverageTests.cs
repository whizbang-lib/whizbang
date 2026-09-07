using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Covers two arms of <see cref="ReceptorInterceptorPipeline"/>'s private strictness switch that
/// <c>ReceptorInterceptorPipelineTests</c> never exercises: the <see cref="DeniedAction.Throw"/>
/// arm (every existing strictness test uses DeadLetter/Quarantine/DropQuiet), and the wildcard
/// default reached only by a <see cref="DeniedAction"/> value outside the four named members.
/// </summary>
public class ReceptorInterceptorPipelineCoverageTests {
  private sealed class StubReceptor { }

  private sealed class DenyingInterceptor(DeniedAction action) : IReceptorInterceptor {
    public ValueTask<InterceptorResult> CanInvokeAsync(Type t, IMessageEnvelope e, IScopeContext? c, CancellationToken ct = default) =>
      new(InterceptorResult.Deny(action));
  }

  /// <summary>
  /// If Throw ever mapped to a strictness at or below DropQuiet's, a receptor whose contract
  /// requires the caller to observe failure (Throw) could be silently outranked by a peer
  /// interceptor willing to drop quiet — turning a required failure into a swallowed no-op.
  /// </summary>
  [Test]
  public async Task EvaluateAll_ThrowOutranksDropQuietAsync() {
    var pipeline = new ReceptorInterceptorPipeline([
      new DenyingInterceptor(DeniedAction.DropQuiet),
      new DenyingInterceptor(DeniedAction.Throw),
    ]);

    var result = await pipeline.EvaluateAllAsync(typeof(StubReceptor), null!, null);

    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.Throw)
      .Because("Throw must outrank DropQuiet when peer interceptors disagree, or a receptor that "
             + "requires caller-visible failure gets silently dropped instead");
  }

  /// <summary>
  /// The default arm is only reachable via an out-of-range/forward-incompatible DeniedAction value
  /// (a future action this build does not know, or a corrupt value). If it silently mapped ABOVE a
  /// known action instead of below every one of them, an unrecognized value could outrank —
  /// and so suppress — a real, audit-required DeadLetter/Quarantine denial.
  /// </summary>
  [Test]
  public async Task EvaluateAll_UnknownDeniedActionValue_NeverOutranksAKnownActionAsync() {
    var unknown = (DeniedAction)99;
    var pipeline = new ReceptorInterceptorPipeline([
      new DenyingInterceptor(unknown),
      new DenyingInterceptor(DeniedAction.DropQuiet),
    ]);

    var result = await pipeline.EvaluateAllAsync(typeof(StubReceptor), null!, null);

    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.DropQuiet)
      .Because("an unmapped DeniedAction value must default to the LEAST strict outcome — mapping "
             + "it above a named action would let a corrupt or forward-incompatible value outrank "
             + "(and so suppress) a real denial");
  }
}
