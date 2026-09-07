using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.DeadLetters;

/// <summary>
/// The two paths <c>StackNormalizerTests</c> leaves dark: a stack line the frame regex does not
/// match, and a stack whose only surviving frames are Whizbang's own. Both decide whether a dead
/// letter gets a FRAME identity or falls back to a PROSE one, and that choice decides how dead
/// letters group — which is the whole point of the stack id.
/// </summary>
/// <code-under-test>src/Whizbang.Core/DeadLetters/StackNormalizer.cs</code-under-test>
[Category("Shard2")]
public sealed class StackNormalizerCoverageTests {

  // Real stack text is not uniformly shaped: blank lines, wrapped messages and "--- End of stack
  // trace from previous location ---" separators all sit between frames. A non-frame line must be
  // skipped rather than treated as a frame, or the joined sequence picks up noise that differs
  // between two occurrences of the SAME failure -- and two dead letters that should have shared a
  // cohort get different hashes and are triaged as unrelated.
  [Test]
  public async Task Normalize_LinesThatAreNotFrames_AreSkippedWithoutJoiningTheSequenceAsync() {
    const string withNoise = """
      System.InvalidOperationException: order already shipped
   at Ordering.ShipmentReceptor.HandleAsync(ShipOrder cmd)

--- End of stack trace from previous location ---
   at Ordering.OrderPipeline.RunAsync()
""";
    const string withoutNoise = """
      System.InvalidOperationException: order already shipped
   at Ordering.ShipmentReceptor.HandleAsync(ShipOrder cmd)
   at Ordering.OrderPipeline.RunAsync()
""";

    var noisy = StackNormalizer.Normalize(withNoise);
    var clean = StackNormalizer.Normalize(withoutNoise);

    await Assert.That(noisy!.Frames.Count).IsEqualTo(2)
      .Because("only the two real frames may enter the sequence; a blank line and a separator are not frames");
    await Assert.That(noisy.SequenceHash).IsEqualTo(clean!.SequenceHash)
      .Because("the same failure must produce the same stack id whether or not the runtime "
             + "interleaved separator lines, or the two occurrences are triaged as unrelated");
  }

  // A failure entirely inside Whizbang -- no consumer frame anywhere -- must still get a FRAME
  // identity. Whizbang frames are deliberately deferred to a fallback so that a consumer frame,
  // when one exists, wins as the more useful attribution. But if nothing survives, falling through
  // to prose grouping would key the cohort on the exception MESSAGE, and library messages routinely
  // embed ids and counts: every occurrence would then hash differently and land in its own cohort
  // of one, which is exactly the fragmentation the stack id exists to prevent.
  [Test]
  public async Task Normalize_OnlyWhizbangFramesSurvive_UsesTheFirstAsTheIdentityRatherThanProseAsync() {
    const string libraryOnly = """
      System.InvalidOperationException: outbox row 4821 already claimed by instance 7
   at System.Threading.Tasks.Task.ThrowAsync(Exception e)
   at Npgsql.NpgsqlDataReader.ReadAsync(CancellationToken ct)
   at Whizbang.Core.Workers.OutboxDrainWorker.DrainAsync(CancellationToken ct)
   at Whizbang.Core.Workers.OutboxPublishWorker.RunOnceAsync(CancellationToken ct)
""";

    var identity = StackNormalizer.Normalize(libraryOnly);

    await Assert.That(identity!.IsProse).IsFalse()
      .Because("a stack with real frames must never degrade to prose grouping, which keys the "
             + "cohort on a message that embeds a row id and an instance number");
    await Assert.That(identity.Frames.Count).IsEqualTo(1)
      .Because("the fallback contributes exactly the FIRST Whizbang frame, not every one of them");
    await Assert.That(identity.Frames[0]).IsEqualTo("Whizbang.Core.Workers.OutboxDrainWorker.DrainAsync")
      .Because("the deepest Whizbang frame is the closest thing to an attribution when no consumer "
             + "frame exists; taking a later one would group unrelated failures under a shared caller");
  }

  // The counterpart that proves the fallback is a fallback and not the rule: when a consumer frame
  // IS present, it must win outright. If Whizbang frames leaked into the sequence alongside it,
  // every cohort would be keyed partly on library internals, so an internal refactor that changed
  // no consumer behavior would silently re-partition every existing dead letter.
  [Test]
  public async Task Normalize_ConsumerFramePresent_WhizbangFramesDoNotEnterTheSequenceAsync() {
    const string mixed = """
      System.InvalidOperationException: order already shipped
   at Whizbang.Core.Workers.InboxDispatchWorker.DispatchAsync(CancellationToken ct)
   at Ordering.ShipmentReceptor.HandleAsync(ShipOrder cmd)
""";

    var identity = StackNormalizer.Normalize(mixed);

    await Assert.That(identity!.Frames.Count).IsEqualTo(1)
      .Because("only the consumer frame belongs in the sequence");
    await Assert.That(identity.Frames[0]).IsEqualTo("Ordering.ShipmentReceptor.HandleAsync")
      .Because("the consumer frame is the attribution that matters; a Whizbang frame alongside it "
             + "would tie the cohort to library internals an upgrade could change");
  }
}
