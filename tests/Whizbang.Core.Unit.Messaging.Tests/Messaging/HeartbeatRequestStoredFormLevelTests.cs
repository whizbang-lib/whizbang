// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2323: every heartbeat says which stored forms its instance reads (<c>stored_form_level</c> in the registry row's
/// metadata), so the trim gate opens only once every live instance reads trimmed rows. The drivers write
/// <see cref="HeartbeatRequest.MetadataJson"/>, so no caller can leave the marker out.
/// </summary>
/// <docs>data/event-store#trim-gate</docs>
public class HeartbeatRequestStoredFormLevelTests {
  private static readonly Guid _instance = Guid.Parse("01900000-0000-7000-8000-000000002323");

  [Test]
  public async Task MetadataJson_WithoutMetadata_IsTheMarkerAloneAsync() {
    var request = new HeartbeatRequest(_instance, "svc", "host", 1);

    using var json = JsonDocument.Parse(request.MetadataJson());

    await Assert.That(json.RootElement.EnumerateObject().Select(p => p.Name))
      .IsEquivalentTo([HeartbeatRequest.STORED_FORM_LEVEL_KEY, HeartbeatRequest.PARTITION_OWNERSHIP_LEVEL_KEY]);
    await Assert.That(json.RootElement.GetProperty(HeartbeatRequest.STORED_FORM_LEVEL_KEY).GetInt32())
      .IsEqualTo(HeartbeatRequest.STORED_FORM_LEVEL);
  }

  [Test]
  public async Task MetadataJson_KeepsTheCallersKeys_AndTheMarkerIsAlwaysThisBuildsAsync() {
    using var given = JsonDocument.Parse("""{"zone":"a","stored_form_level":0}""");
    var request = new HeartbeatRequest(_instance, "svc", "host", 1, Metadata: given.RootElement);

    using var json = JsonDocument.Parse(request.MetadataJson());

    await Assert.That(json.RootElement.GetProperty("zone").GetString()).IsEqualTo("a");
    await Assert.That(json.RootElement.GetProperty(HeartbeatRequest.STORED_FORM_LEVEL_KEY).GetInt32())
      .IsEqualTo(HeartbeatRequest.STORED_FORM_LEVEL).Because("the marker says what this build reads; a caller cannot lower it");
    await Assert.That(json.RootElement.EnumerateObject().Count()).IsEqualTo(3).Because("each marker is written once");
  }

  [Test]
  public async Task MetadataJson_MetadataThatIsNotAnObject_IsReplacedByTheMarkerAsync() {
    using var given = JsonDocument.Parse("[1,2]");
    var request = new HeartbeatRequest(_instance, "svc", "host", 1, Metadata: given.RootElement);

    using var json = JsonDocument.Parse(request.MetadataJson());

    await Assert.That(json.RootElement.GetProperty(HeartbeatRequest.STORED_FORM_LEVEL_KEY).GetInt32())
      .IsEqualTo(HeartbeatRequest.STORED_FORM_LEVEL).Because("the registry row's metadata is an object; the marker must be in it");
  }

  [Test]
  public async Task StoredFormLevel_IsTwo_TheLevelThatReadsStrongIdsAsUuidStringsAsync() {
    using var json = JsonDocument.Parse(new HeartbeatRequest(_instance, "svc", "host", 1).MetadataJson());

    await Assert.That(json.RootElement.GetProperty("stored_form_level").GetInt32()).IsEqualTo(2)
      .Because("migration 537's trim gate opens for level 1 or higher and migration 562's strong-id gate for level 2 or "
        + "higher, under this key; a later stored form raises it, never lowers it");
  }

  /// <summary>
  /// #2518: the heartbeat says the process writes strongly-typed ids as UUID strings only once it does, so the
  /// stored-form rewrite waits for every live instance to have switched.
  /// </summary>
  [Test]
  public async Task MetadataJson_SaysItWritesUuidStrings_OnlyOnceItDoesAsync() {
    var request = new HeartbeatRequest(_instance, "svc", "host", 1,
      JsonElement.Parse("""{"strong_id_document_form":"uuid-string","zone":"a"}"""));

    using var before = JsonDocument.Parse(request.MetadataJson(writesUuidStrings: false));
    using var after = JsonDocument.Parse(request.MetadataJson(writesUuidStrings: true));

    await Assert.That(before.RootElement.TryGetProperty(HeartbeatRequest.STRONG_ID_DOCUMENT_FORM_KEY, out _)).IsFalse()
      .Because("a caller cannot claim the form; only the process's own switch says it");
    await Assert.That(before.RootElement.GetProperty("zone").GetString()).IsEqualTo("a");
    await Assert.That(after.RootElement.GetProperty("strong_id_document_form").GetString()).IsEqualTo("uuid-string")
      .Because("migration 562's rewrite readiness reads this key and value");
    await Assert.That(after.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList()).IsEquivalentTo([
      HeartbeatRequest.PARTITION_OWNERSHIP_LEVEL_KEY, HeartbeatRequest.STORED_FORM_LEVEL_KEY,
      HeartbeatRequest.STRONG_ID_DOCUMENT_FORM_KEY, "zone"]).Because("each marker is written once, beside the caller's keys");
  }

  [Test]
  public async Task PartitionOwnershipLevel_IsOne_TheLevelThatSupportsOwnershipByMapAsync() {
    using var given = JsonDocument.Parse("""{"partition_ownership_level":0}""");
    using var json = JsonDocument.Parse(new HeartbeatRequest(_instance, "svc", "host", 1, Metadata: given.RootElement).MetadataJson());

    await Assert.That(json.RootElement.GetProperty("partition_ownership_level").GetInt32()).IsEqualTo(1)
      .Because("migration 565's switch turns ownership by map on only once every live instance says level 1 or higher under this key (#2555)");
    await Assert.That(json.RootElement.EnumerateObject().Count(p => p.Name == HeartbeatRequest.PARTITION_OWNERSHIP_LEVEL_KEY)).IsEqualTo(1);
  }
}
