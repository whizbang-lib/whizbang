using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Pattern A conversion for <see cref="IChaosInjector"/>.
/// </summary>
/// <remarks>
/// <para>
/// This dependency used null as a signal: <c>IsActive</c> read "an injector is registered" as
/// "the reference is not null". A no-op default therefore could not simply be dropped in, because
/// a registered no-op would report chaos as active when nothing is injecting anything, and workers
/// would take the chaos path in production.
/// </para>
/// <para>
/// The capability moves onto the interface instead, so absence is expressed as a value that says
/// what it is rather than as a missing reference that has to be inferred.
/// </para>
/// </remarks>
/// <docs>operations/testing/chaos-injection</docs>
[Category("Messaging")]
public class ChaosInjectorDefaultTests {

  [Test]
  public async Task TheDefaultInjectorReportsChaosInactiveEvenWhenHooksAreEnabledAsync() {
    var options = Options.Create(new WhizbangOptions());
    options.Value.Guardrails.EnableChaosHooks = true;

    var invoker = new ChaosInjectorInvoker(options, NoChaosInjector.Instance);

    // The flag says hooks are permitted; nothing is actually injecting. Reporting active here
    // would send every worker down the chaos path in production.
    await Assert.That(invoker.IsActive).IsFalse()
      .Because("a registered no-op injector means no chaos is being injected, which is exactly "
             + "what an unregistered injector used to mean");
  }

  [Test]
  public async Task ARealInjectorReportsActiveWhenHooksAreEnabledAsync() {
    var options = Options.Create(new WhizbangOptions());
    options.Value.Guardrails.EnableChaosHooks = true;

    var invoker = new ChaosInjectorInvoker(options, new RecordingInjector());

    await Assert.That(invoker.IsActive).IsTrue();
  }

  [Test]
  public async Task ARealInjectorIsInactiveWhileHooksAreDisabledAsync() {
    var options = Options.Create(new WhizbangOptions());
    options.Value.Guardrails.EnableChaosHooks = false;

    var invoker = new ChaosInjectorInvoker(options, new RecordingInjector());

    // The options flag still wins; registering an injector must not turn chaos on by itself.
    await Assert.That(invoker.IsActive).IsFalse();
  }

  [Test]
  public async Task TheDefaultInjectorIsNotInvokedAtACheckpointAsync() {
    var options = Options.Create(new WhizbangOptions());
    options.Value.Guardrails.EnableChaosHooks = true;
    var invoker = new ChaosInjectorInvoker(options, NoChaosInjector.Instance);

    await invoker.BeforeCheckpointAsync("checkpoint", payload: null, CancellationToken.None);

    await Assert.That(invoker.IsActive).IsFalse();
  }

  [Test]
  public async Task ARealInjectorIsInvokedAtACheckpointAsync() {
    var options = Options.Create(new WhizbangOptions());
    options.Value.Guardrails.EnableChaosHooks = true;
    var injector = new RecordingInjector();
    var invoker = new ChaosInjectorInvoker(options, injector);

    await invoker.BeforeCheckpointAsync("checkpoint-a", payload: null, CancellationToken.None);

    await Assert.That(injector.Checkpoints).Contains("checkpoint-a");
  }

  private sealed class RecordingInjector : IChaosInjector {
    public List<string> Checkpoints { get; } = [];

    public ValueTask BeforeCheckpointAsync(string checkpoint, object? payload, CancellationToken cancellationToken) {
      Checkpoints.Add(checkpoint);
      return ValueTask.CompletedTask;
    }
  }
}
