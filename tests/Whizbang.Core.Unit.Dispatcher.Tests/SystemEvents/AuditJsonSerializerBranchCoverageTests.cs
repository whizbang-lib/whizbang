// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Branch coverage for <see cref="AuditJsonSerializer"/>: an unserializable payload with no logger
/// supplied still degrades to an empty audit body instead of faulting the audit write.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditJsonSerializer.cs</code-under-test>
[Category("SystemEvents")]
public class AuditJsonSerializerBranchCoverageTests {

  private sealed record UnregisteredAuditPayload(string Value);

  /// <summary>
  /// The empty-payload warning is written through a no-op logger when the caller has none
  /// (the outbox builder calls it that way). If that fallback went missing the warning call
  /// would dereference null and the whole audit record would be lost instead of written empty.
  /// </summary>
  [Test]
  public async Task SerializeToJsonElement_UnregisteredTypeWithNoLogger_ReturnsEmptyObjectAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var result = AuditJsonSerializer.SerializeToJsonElement(new UnregisteredAuditPayload("x"), options, logger: null);

    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(result.EnumerateObject().Any()).IsFalse()
      .Because("with no logger the payload still degrades to an empty {} body rather than throwing");
  }
}
