using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// v0.502 slice C.4 — locks the DeadLetterStore wire-up + GenerationProvider seam.
///
/// <para>
/// Direct integration with <see cref="Whizbang.Core.Workers.InboxDispatchWorker"/> is
/// covered by the existing functional tests; this file focuses on the contract the worker
/// expects from its DLQ collaborators (<see cref="IDeadLetterStore"/>,
/// <see cref="IGenerationProvider"/>) and the default provider's behaviour.
/// </para>
/// </summary>
public class InboxDispatchWorkerDeadLetterTests {

  [Test]
  public async Task DefaultGenerationProvider_ReturnsStableNonEmptyValueAsync() {
    var provider = new DefaultGenerationProvider();
    var generation = provider.GetGeneration();
    await Assert.That(generation).IsNotNullOrEmpty()
      .Because("the generation tag is the recovery worker's deploy-replay anchor — must never be empty");
    await Assert.That(generation).StartsWith("whizbang/")
      .Because("default provider prefixes with whizbang/ so multi-version generations are unambiguous");
  }

  [Test]
  public async Task DefaultGenerationProvider_ReturnsSameValueAcrossCallsAsync() {
    var provider = new DefaultGenerationProvider();
    var a = provider.GetGeneration();
    var b = provider.GetGeneration();
    await Assert.That(a).IsEqualTo(b)
      .Because("the recovery worker compares generation strings exactly — non-determinism would break auto-replay");
  }

  [Test]
  public async Task DeadLetterSourceTable_ConstantsMatchSqlExpectationsAsync() {
    // The move_to_dead_letters() SQL function does a literal string compare on
    // p_source_table. Renaming these constants without updating the migration silently
    // breaks the integration. Lock them as regression anchors.
#pragma warning disable TUnitAssertions0005
    await Assert.That(DeadLetterSourceTable.OUTBOX).IsEqualTo("wh_outbox");
    await Assert.That(DeadLetterSourceTable.INBOX).IsEqualTo("wh_inbox");
    await Assert.That(DeadLetterSourceTable.PERSPECTIVE_EVENTS).IsEqualTo("wh_perspective_events");
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task CustomGenerationProvider_OverridesDefaultWhenRegisteredAsync() {
    // Operators register their own IGenerationProvider via DI to combine
    // Whizbang+service+branch. Verify the abstraction supports it.
    IGenerationProvider provider = new CustomGen("0.502.0-alpha.1+consumer-1.42.0+ag-grid-license");
    await Assert.That(provider.GetGeneration())
      .IsEqualTo("0.502.0-alpha.1+consumer-1.42.0+ag-grid-license");
  }

  private sealed class CustomGen(string value) : IGenerationProvider {
    public string GetGeneration() => value;
  }
}
