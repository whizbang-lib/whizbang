using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="ChaosInjectorInvoker"/> — the small indirection
/// workers use to gate IChaosInjector calls behind the production-safe
/// EnableChaosHooks flag. The invariant: in production, with the flag off, the
/// invoker MUST short-circuit without touching the injector — even if one is
/// registered. This guarantees zero cost on the hot path.
///
/// Coverage report showed 0/11 lines — workers exercise it indirectly but no
/// direct test pinned the two short-circuit paths (flag off, injector null) or
/// the active path (flag on + injector present).
/// </summary>
/// <docs>operations/testing/chaos-injection</docs>
public class ChaosInjectorInvokerTests {

  [Test]
  public async Task IsActive_FlagOff_NoInjector_FalseAsync() {
    var sut = new ChaosInjectorInvoker(_options(enabled: false), injector: NoChaosInjector.Instance);

    await Assert.That(sut.IsActive).IsFalse();
  }

  [Test]
  public async Task IsActive_FlagOff_WithInjector_FalseAsync() {
    // Even with an injector registered, the flag governs activation.
    var sut = new ChaosInjectorInvoker(_options(enabled: false), new _CountingInjector());

    await Assert.That(sut.IsActive).IsFalse();
  }

  [Test]
  public async Task IsActive_FlagOn_NoInjector_FalseAsync() {
    var sut = new ChaosInjectorInvoker(_options(enabled: true), injector: NoChaosInjector.Instance);

    await Assert.That(sut.IsActive).IsFalse();
  }

  [Test]
  public async Task IsActive_FlagOn_WithInjector_TrueAsync() {
    var sut = new ChaosInjectorInvoker(_options(enabled: true), new _CountingInjector());

    await Assert.That(sut.IsActive).IsTrue();
  }

  [Test]
  public async Task IsActive_NullOptions_FalseAsync() {
    // The (?.Value?.Guardrails) coalesces to false — null options = production safe.
    var sut = new ChaosInjectorInvoker(options: null, new _CountingInjector());

    await Assert.That(sut.IsActive).IsFalse();
  }

  [Test]
  public async Task BeforeCheckpointAsync_Inactive_DoesNotCallInjectorAsync() {
    var injector = new _CountingInjector();
    var sut = new ChaosInjectorInvoker(_options(enabled: false), injector);

    await sut.BeforeCheckpointAsync("Worker.Checkpoint", payload: null, CancellationToken.None);

    await Assert.That(injector.CallCount).IsEqualTo(0);
  }

  [Test]
  public async Task BeforeCheckpointAsync_NullInjector_NoOpAsync() {
    var sut = new ChaosInjectorInvoker(_options(enabled: true), injector: NoChaosInjector.Instance);

    // No throw, no measurable side effect — call returns ValueTask.CompletedTask.
    await sut.BeforeCheckpointAsync("Worker.Checkpoint", payload: null, CancellationToken.None);
  }

  [Test]
  public async Task BeforeCheckpointAsync_Active_DelegatesToInjectorWithCheckpointNameAsync() {
    var injector = new _CountingInjector();
    var sut = new ChaosInjectorInvoker(_options(enabled: true), injector);

    await sut.BeforeCheckpointAsync("PerspectiveWorker.BeforeBatch", payload: 42, CancellationToken.None);

    await Assert.That(injector.CallCount).IsEqualTo(1);
    await Assert.That(injector.LastCheckpoint).IsEqualTo("PerspectiveWorker.BeforeBatch");
    await Assert.That(injector.LastPayload).IsEqualTo(42);
  }

  private static IOptions<WhizbangOptions> _options(bool enabled) =>
    Options.Create(new WhizbangOptions {
      Guardrails = new WhizbangGuardrailsOptions {
        EnableChaosHooks = enabled,
      },
    });

  private sealed class _CountingInjector : IChaosInjector {
    public int CallCount { get; private set; }
    public string? LastCheckpoint { get; private set; }
    public object? LastPayload { get; private set; }
    public ValueTask BeforeCheckpointAsync(string checkpoint, object? payload, CancellationToken cancellationToken) {
      CallCount++;
      LastCheckpoint = checkpoint;
      LastPayload = payload;
      return ValueTask.CompletedTask;
    }
  }
}
