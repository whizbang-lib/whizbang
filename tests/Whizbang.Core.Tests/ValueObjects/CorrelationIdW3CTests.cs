using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// A correlation id may originate outside the system (an inbound <c>X-Correlation-ID</c> header, a W3C
/// trace-id, or a browser <c>crypto.randomUUID()</c> which is UUIDv4). Such external tokens must be accepted
/// without the UUIDv7 validation that <see cref="CorrelationId.From(System.Guid)"/> enforces for
/// internally-minted ids, and internally-minted root ids should adopt the ambient W3C trace-id.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
public class CorrelationIdW3CTests {

  [Test]
  public async Task FromExternal_WithUuidV4_PreservesValueWithoutThrowingAsync() {
    var v4 = Guid.NewGuid(); // UUIDv4

    var correlation = CorrelationId.FromExternal(v4);

    await Assert.That(correlation.Value).IsEqualTo(v4)
      .Because("An external correlation token (e.g. the browser's crypto.randomUUID v4) must be accepted verbatim.");
  }

  [Test]
  public async Task NewRootAligned_WithActiveTrace_AdoptsTheTraceIdAsync() {
    using var source = new ActivitySource("Whizbang.Core.Tests.Correlation");
    using var listener = new ActivityListener {
      ShouldListenTo = _ => true,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    using var activity = source.StartActivity("op");
    await Assert.That(activity).IsNotNull();

    var correlation = CorrelationId.NewRootAligned();

    Span<byte> traceBytes = stackalloc byte[16];
    activity!.TraceId.CopyTo(traceBytes);
    var expected = new Guid(traceBytes, bigEndian: true);

    await Assert.That(correlation.Value).IsEqualTo(expected)
      .Because("With an active Activity, the root correlation id adopts its 128-bit W3C trace-id.");
  }

  [Test]
  public async Task NewRootAligned_WithNoTrace_MintsFreshUuidV7Async() {
    // No ActivityListener registered here, so Activity.Current is null → falls back to a fresh v7.
    var a = CorrelationId.NewRootAligned();
    var b = CorrelationId.NewRootAligned();

    await Assert.That(a).IsNotEqualTo(b);
    await Assert.That(a.Value.Version).IsEqualTo(7);
  }
}
