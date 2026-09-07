using System;
using System.Collections.Generic;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tail-of-round coverage for <see cref="ControlMessageTtl"/>: the public metadata-key accessor
/// and the string-encoded-seconds branch of <see cref="ControlMessageTtl.FromMetadata"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/ControlMessageTtl.cs</code-under-test>
public class ControlMessageTtlCoverageTests {

  /// <summary>
  /// Transports lift the stamped lifetime into their native expiry by reading this key off
  /// destination metadata; if the property drifted from the underlying constant, a transport
  /// keyed off it would never find the stamp, and the message would silently fall back to
  /// whatever expiry the broker defaults to.
  /// </summary>
  [Test]
  public async Task MetadataKey_ExposesTheUnderlyingConstantAsync() {
    await Assert.That(ControlMessageTtl.MetadataKey).IsEqualTo("whizbang.time-to-live-seconds");
  }

  /// <summary>
  /// A minted lifetime can arrive JSON-encoded as a string rather than a number depending on the
  /// serializer that wrote the metadata bag. Failing to parse the string form silently degrades
  /// to "no lifetime" — the message then rides the broker default TTL instead of the one Core
  /// actually minted for it, which is exactly the ambiguity <c>FromMetadata</c> exists to avoid.
  /// </summary>
  [Test]
  public async Task FromMetadata_StringEncodedSeconds_ParsesTheValueAsync() {
    var metadata = new Dictionary<string, JsonElement> {
      [ControlMessageTtl.MetadataKey] = JsonDocument.Parse("\"45\"").RootElement,
    };

    var ttl = ControlMessageTtl.FromMetadata(metadata);

    await Assert.That(ttl).IsEqualTo(TimeSpan.FromSeconds(45));
  }
}
