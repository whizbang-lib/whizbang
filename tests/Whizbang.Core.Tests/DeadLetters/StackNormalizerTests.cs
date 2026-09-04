using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.DeadLetters;

/// <summary>
/// <para>Locks the ONE stack-normalization implementation (P2 of
/// plans/dlq-stack-intelligence.md). The stack pipeline deliberately lives in C# only —
/// the inline metric at dead-letter time and the maintenance backfill must produce the
/// SAME stack_id, and a C#/SQL dual implementation would drift and split cohorts
/// silently. All frames are kept (no 3-frame cap: depth limits belong to the legacy
/// fingerprint, not the relational layer), async state machinery normalizes, and prose
/// errors get a scrubbed-template identity so every dead letter has a stack_id.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/DeadLetters/StackNormalizer.cs</code-under-test>
[Category("Shard2")]
public sealed class StackNormalizerTests {

  [Test]
  public async Task TypedError_KeepsAllConsumerFrames_NormalizedAsync() {
    var text = "System.InvalidOperationException: boom\n"
      + "   at MyApp.Orders.OrderProcessor.<ApplyAsync>d__12.MoveNext()\n"
      + "   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification()\n"
      + "   at MyApp.Orders.OrderService.<HandleAsync>d__4.MoveNext()\n"
      + "   at MyApp.Api.Endpoint.PostAsync(Request r)\n"
      + "   at MyApp.Api.Pipeline.RunAsync()\n"
      + "   at MyApp.Api.Host.MainAsync()";

    var stack = StackNormalizer.Normalize(text)!;

    await Assert.That(stack.Frames.Count).IsEqualTo(5)
      .Because("ALL consumer frames survive — the relational layer has no 3-frame cap; "
             + "framework frames are the only exclusions");
    await Assert.That(stack.Frames[0]).IsEqualTo("MyApp.Orders.OrderProcessor.ApplyAsync")
      .Because("async state machinery (<M>d__N.MoveNext) normalizes to the method — "
             + "d__N changes on recompile and would split a cohort across builds");
    await Assert.That(stack.IsProse).IsFalse();
  }

  [Test]
  public async Task SequenceHash_IsStableAcrossRecompiles_AndOrderAwareAsync() {
    var build1 = "System.InvalidOperationException: x\n"
      + "   at A.B.<M1>d__1.MoveNext()\n   at A.C.<M2>d__2.MoveNext()";
    var build2 = "System.InvalidOperationException: x\n"
      + "   at A.B.<M1>d__9.MoveNext()\n   at A.C.<M2>d__7.MoveNext()";
    var reordered = "System.InvalidOperationException: x\n"
      + "   at A.C.<M2>d__2.MoveNext()\n   at A.B.<M1>d__1.MoveNext()";

    await Assert.That(StackNormalizer.Normalize(build1)!.SequenceHash)
      .IsEqualTo(StackNormalizer.Normalize(build2)!.SequenceHash);
    await Assert.That(StackNormalizer.Normalize(build1)!.SequenceHash)
      .IsNotEqualTo(StackNormalizer.Normalize(reordered)!.SequenceHash)
      .Because("throw-site-versus-caller order is semantic: the hash is over the ORDERED "
             + "sequence, unlike a set digest");
  }

  [Test]
  public async Task ProseError_GetsATemplateIdentity_WithNoFramesAsync() {
    var a = StackNormalizer.Normalize(
      "Attempt 1 ended without a reported outcome: lease held by instance "
      + "01a064d6-57d0-75e4-86f4-d82890e6e1f2 expired at 2026-09-03 03:58:23+00")!;
    var b = StackNormalizer.Normalize(
      "Attempt 9 ended without a reported outcome: lease held by instance "
      + "9f00aa11-2233-4455-8677-889900aabbcc expired at 2026-09-04 01:02:03+00")!;

    await Assert.That(a.IsProse).IsTrue();
    await Assert.That(a.Frames.Count).IsEqualTo(0);
    await Assert.That(a.SequenceHash).IsEqualTo(b.SequenceHash)
      .Because("every dead letter carries a stack_id: for prose the identity is the "
             + "scrubbed template, so the metric and the analytics stay universal");
  }

  [Test]
  public async Task Hash_IsSixteenLowerHexAsync() {
    var stack = StackNormalizer.Normalize("Anything at all")!;
    await Assert.That(stack.SequenceHash.Length).IsEqualTo(16);
    await Assert.That(stack.SequenceHash.All(c => "0123456789abcdef".Contains(c))).IsTrue()
      .Because("same shape as the SQL fingerprint keys — joinable, indexable, log-friendly");
  }

  [Test]
  public async Task NullOrEmpty_YieldsNullAsync() {
    await Assert.That(StackNormalizer.Normalize(null)).IsNull();
    await Assert.That(StackNormalizer.Normalize("   ")).IsNull()
      .Because("no error text is no identity — a placeholder hash would create a giant "
             + "false cohort of unrelated failures");
  }
}
