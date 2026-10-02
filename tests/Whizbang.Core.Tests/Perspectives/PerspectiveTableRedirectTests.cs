using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The ambient table redirect a blue-green rebuild sets so the stores of its flow write the shadow table, while
/// every other flow keeps the live one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveTableRedirect.cs</code-under-test>
[Category("Unit")]
[Category("Perspectives")]
public class PerspectiveTableRedirectTests {
  [Test]
  public async Task Resolve_WithoutARedirect_ReturnsTheTableAsync() {
    await Assert.That(PerspectiveTableRedirect.IsActive).IsFalse();
    await Assert.That(PerspectiveTableRedirect.Resolve("wh_per_order")).IsEqualTo("wh_per_order");
  }

  [Test]
  public async Task Resolve_RedirectsTheBareQualifiedAndQuotedForms_KeepingPrefixAndQuotingAsync() {
    using (PerspectiveTableRedirect.Begin("wh_per_order", "wh_per_order_bg")) {
      await Assert.That(PerspectiveTableRedirect.IsActive).IsTrue();
      await Assert.That(PerspectiveTableRedirect.Resolve("wh_per_order")).IsEqualTo("wh_per_order_bg");
      await Assert.That(PerspectiveTableRedirect.Resolve("svc.wh_per_order")).IsEqualTo("svc.wh_per_order_bg");
      await Assert.That(PerspectiveTableRedirect.Resolve("\"svc\".\"wh_per_order\"")).IsEqualTo("\"svc\".\"wh_per_order_bg\"");
      await Assert.That(PerspectiveTableRedirect.Resolve("\"wh_per_order")).IsEqualTo("\"wh_per_order")
        .Because("A half-quoted name is not the table's name, so it is not redirected.");
      await Assert.That(PerspectiveTableRedirect.Resolve("wh_per_other")).IsEqualTo("wh_per_other")
        .Because("Only the table being rebuilt is redirected.");
    }
    await Assert.That(PerspectiveTableRedirect.Resolve("wh_per_order")).IsEqualTo("wh_per_order")
      .Because("Disposing the handle ends the redirect.");
  }

  [Test]
  public async Task Begin_Nested_ResolvesBoth_AndRestoresTheOuterOnDisposeAsync() {
    using (PerspectiveTableRedirect.Begin("a", "a_bg")) {
      using (PerspectiveTableRedirect.Begin("b", "b_bg")) {
        await Assert.That(PerspectiveTableRedirect.Resolve("a")).IsEqualTo("a_bg");
        await Assert.That(PerspectiveTableRedirect.Resolve("b")).IsEqualTo("b_bg");
      }
      await Assert.That(PerspectiveTableRedirect.Resolve("b")).IsEqualTo("b");
      await Assert.That(PerspectiveTableRedirect.Resolve("a")).IsEqualTo("a_bg");
    }
  }

  [Test]
  public async Task Begin_DoesNotReachAFlowThatWasAlreadyRunningAsync() {
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var other = Task.Run(async () => {
      await gate.Task;
      return PerspectiveTableRedirect.Resolve("wh_per_order");
    });

    using (PerspectiveTableRedirect.Begin("wh_per_order", "wh_per_order_bg")) {
      gate.SetResult();
      await Assert.That(await other).IsEqualTo("wh_per_order")
        .Because("A redirect belongs to the flow that set it; a live worker keeps the live table.");
    }
  }

  [Test]
  public async Task Begin_And_Resolve_RejectBadArgumentsAsync() {
    await Assert.That(() => PerspectiveTableRedirect.Begin(" ", "t")).ThrowsExactly<ArgumentException>();
    await Assert.That(() => PerspectiveTableRedirect.Begin("t", "")).ThrowsExactly<ArgumentException>();
    await Assert.That(() => PerspectiveTableRedirect.Resolve(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
