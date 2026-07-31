using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Stream-integrity Phase B: an <see cref="IntegrityCheckpoint"/> (a Core framework event with a
/// nested bucket list) must round-trip through the combined JSON options — the wire fidelity every
/// consumer-side comparison depends on. Locks the generator's transitive registration of
/// <see cref="CheckpointBucket"/> and its list.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
[Category("Messaging")]
public class IntegrityCheckpointWireSerializationTests {

  [Test]
  public async Task IntegrityCheckpoint_RoundTripsThroughCombinedOptionsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var origin = TrackedGuid.NewMedo().Value;
    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = origin,
      OriginServiceId = origin,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 100,
      ToCommitSequence = 250,
      Buckets = [
        new CheckpointBucket { TenantScope = "tenant-a", EventType = "Contracts.TypeX", Count = 42 },
        new CheckpointBucket { TenantScope = null, EventType = "Contracts.TypeY", Count = 7 },
      ],
    };

    var json = JsonSerializer.Serialize(checkpoint, options.GetTypeInfo(typeof(IntegrityCheckpoint)));
    var back = (IntegrityCheckpoint)JsonSerializer.Deserialize(json, options.GetTypeInfo(typeof(IntegrityCheckpoint)))!;

    await Assert.That(back.OriginServiceId).IsEqualTo(origin);
    await Assert.That(back.OriginServiceName).IsEqualTo("origin-svc");
    await Assert.That(back.FromCommitSequence).IsEqualTo(100L);
    await Assert.That(back.ToCommitSequence).IsEqualTo(250L);
    await Assert.That(back.Buckets.Count).IsEqualTo(2)
      .Because("the nested bucket list is the checkpoint's whole payload — losing it silently " +
               "would turn every window into a false all-clear.");
    await Assert.That(back.Buckets[0].TenantScope).IsEqualTo("tenant-a");
    await Assert.That(back.Buckets[0].EventType).IsEqualTo("Contracts.TypeX");
    await Assert.That(back.Buckets[0].Count).IsEqualTo(42);
    await Assert.That(back.Buckets[1].TenantScope).IsNull();
  }
}
