using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncInquiry"/> and <see cref="SyncInquiryResult"/> DTOs.
/// </summary>
/// <remarks>
/// These DTOs are used to check if events have been processed by perspectives
/// via the batch function (database-based sync).
/// </remarks>
/// <docs>perspectives/sync</docs>
public class SyncInquiryTests {
  // ==========================================================================
  // SyncInquiry record tests
  // ==========================================================================

  [Test]
  public async Task SyncInquiry_DefaultInquiryId_GeneratesNewGuidAsync() {
    var streamId = Guid.NewGuid();
    var inquiry1 = new SyncInquiry { StreamId = streamId, PerspectiveName = "Test" };
    var inquiry2 = new SyncInquiry { StreamId = streamId, PerspectiveName = "Test" };

    await Assert.That(inquiry1.InquiryId).IsNotEqualTo(Guid.Empty)
      .Because("InquiryId should be auto-generated");
    await Assert.That(inquiry1.InquiryId).IsNotEqualTo(inquiry2.InquiryId)
      .Because("Each inquiry should get a unique InquiryId");
  }

  [Test]
  public async Task SyncInquiry_DefaultIncludePendingEventIds_IsFalseAsync() {
    var inquiry = new SyncInquiry { StreamId = Guid.NewGuid(), PerspectiveName = "Test" };

    await Assert.That(inquiry.IncludePendingEventIds).IsFalse()
      .Because("IncludePendingEventIds should default to false for performance");
  }

  [Test]
  public async Task SyncInquiry_WithEventIds_SetsEventIdsAsync() {
    var eventIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var inquiry = new SyncInquiry {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Test",
      EventIds = eventIds
    };

    await Assert.That(inquiry.EventIds).IsNotNull();
    await Assert.That(inquiry.EventIds!.Length).IsEqualTo(2);
    await Assert.That(inquiry.EventIds).IsEquivalentTo(eventIds);
  }

  [Test]
  public async Task SyncInquiry_RequiredProperties_MustBeSetAsync() {
    var streamId = Guid.NewGuid();
    const string perspectiveName = "OrderPerspective";

    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName
    };

    await Assert.That(inquiry.StreamId).IsEqualTo(streamId);
    await Assert.That(inquiry.PerspectiveName).IsEqualTo(perspectiveName);
  }

  [Test]
  public async Task SyncInquiry_WithIncludePendingEventIdsTrue_StoresValueAsync() {
    var inquiry = new SyncInquiry {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Test",
      IncludePendingEventIds = true
    };

    await Assert.That(inquiry.IncludePendingEventIds).IsTrue();
  }

  [Test]
  public async Task SyncInquiry_WithExplicitInquiryId_UsesProvidedIdAsync() {
    var explicitId = Guid.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Test",
      InquiryId = explicitId
    };

    await Assert.That(inquiry.InquiryId).IsEqualTo(explicitId);
  }

  // ==========================================================================
  // SyncInquiryResult record tests
  // ==========================================================================

  [Test]
  public async Task SyncInquiryResult_NoPendingEvents_IsFullySyncedAsync() {
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0
    };

    await Assert.That(result.IsFullySynced).IsTrue()
      .Because("PendingCount == 0 means all events are processed");
  }

  [Test]
  public async Task SyncInquiryResult_HasPendingEvents_NotFullySyncedAsync() {
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 5
    };

    await Assert.That(result.IsFullySynced).IsFalse()
      .Because("PendingCount > 0 means some events are still waiting");
  }

  [Test]
  public async Task SyncInquiryResult_WithPendingEventIds_StoresIdsAsync() {
    var pendingIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 3,
      PendingEventIds = pendingIds
    };

    await Assert.That(result.PendingEventIds).IsNotNull();
    await Assert.That(result.PendingEventIds!.Length).IsEqualTo(3);
    await Assert.That(result.PendingEventIds).IsEquivalentTo(pendingIds);
  }

  [Test]
  public async Task SyncInquiryResult_WithoutPendingEventIds_DefaultsToNullAsync() {
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 2
    };

    await Assert.That(result.PendingEventIds).IsNull()
      .Because("PendingEventIds is optional and defaults to null");
  }

  [Test]
  public async Task SyncInquiryResult_InquiryId_CorrelatesWithRequestAsync() {
    var correlationId = Guid.NewGuid();
    var result = new SyncInquiryResult {
      InquiryId = correlationId,
      PendingCount = 0
    };

    await Assert.That(result.InquiryId).IsEqualTo(correlationId)
      .Because("InquiryId should match the request's InquiryId for correlation");
  }

  [Test]
  public async Task SyncInquiryResult_IsFullySynced_IsDerivedPropertyAsync() {
    // Verify IsFullySynced is computed, not stored
    var result1 = new SyncInquiryResult { InquiryId = Guid.NewGuid(), PendingCount = 0 };
    var result2 = new SyncInquiryResult { InquiryId = Guid.NewGuid(), PendingCount = 1 };

    await Assert.That(result1.IsFullySynced).IsTrue();
    await Assert.That(result2.IsFullySynced).IsFalse();

    // Verify the property is derived from PendingCount
    await Assert.That(result1.IsFullySynced).IsEqualTo(result1.PendingCount == 0);
    await Assert.That(result2.IsFullySynced).IsEqualTo(result2.PendingCount == 0);
  }

  // ==========================================================================
  // SyncInquiry - IncludeProcessedEventIds tests
  // ==========================================================================

  [Test]
  public async Task SyncInquiry_DefaultIncludeProcessedEventIds_IsFalseAsync() {
    var inquiry = new SyncInquiry { StreamId = Guid.NewGuid(), PerspectiveName = "Test" };

    await Assert.That(inquiry.IncludeProcessedEventIds).IsFalse()
      .Because("IncludeProcessedEventIds should default to false for performance");
  }

  [Test]
  public async Task SyncInquiry_WithIncludeProcessedEventIdsTrue_StoresValueAsync() {
    var inquiry = new SyncInquiry {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Test",
      IncludeProcessedEventIds = true
    };

    await Assert.That(inquiry.IncludeProcessedEventIds).IsTrue();
  }

  // ==========================================================================
  // SyncInquiryResult - ExpectedEventIds / ProcessedEventIds tests
  // ==========================================================================

  [Test]
  public async Task SyncInquiryResult_WithExpectedEventIds_AllProcessed_IsFullySyncedAsync() {
    // All expected events are in the processed set
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ExpectedEventIds = [eventId1, eventId2],
      ProcessedEventIds = [eventId1, eventId2]
    };

    await Assert.That(result.IsFullySynced).IsTrue()
      .Because("All expected events are processed");
  }

  [Test]
  public async Task SyncInquiryResult_WithExpectedEventIds_NoneProcessed_NotFullySyncedAsync() {
    // No expected events are in the processed set (events not in DB yet)
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,  // No rows in wh_perspective_events yet
      ExpectedEventIds = [eventId1, eventId2],
      ProcessedEventIds = []  // Empty - events not processed
    };

    await Assert.That(result.IsFullySynced).IsFalse()
      .Because("Expected events are not yet processed (not even in DB)");
  }

  [Test]
  public async Task SyncInquiryResult_WithExpectedEventIds_PartiallyProcessed_NotFullySyncedAsync() {
    // Only some expected events are processed
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 1,  // One still pending
      ExpectedEventIds = [eventId1, eventId2],
      ProcessedEventIds = [eventId1]  // Only first one processed
    };

    await Assert.That(result.IsFullySynced).IsFalse()
      .Because("Not all expected events are processed yet");
  }

  [Test]
  public async Task SyncInquiryResult_WithNullExpectedEventIds_FallsBackToPendingCountAsync() {
    // Legacy behavior: no expected event IDs, use PendingCount == 0
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ExpectedEventIds = null,
      ProcessedEventIds = null
    };

    await Assert.That(result.IsFullySynced).IsTrue()
      .Because("With no ExpectedEventIds, falls back to PendingCount == 0");
  }

  [Test]
  public async Task SyncInquiryResult_WithEmptyExpectedEventIds_FallsBackToPendingCountAsync() {
    // Empty array also falls back to legacy behavior
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ExpectedEventIds = [],
      ProcessedEventIds = null
    };

    await Assert.That(result.IsFullySynced).IsTrue()
      .Because("Empty ExpectedEventIds falls back to PendingCount == 0");
  }

  [Test]
  public async Task SyncInquiryResult_WithNullProcessedEventIds_NotFullySyncedAsync() {
    // If we expect events but ProcessedEventIds is null, not synced
    var eventId1 = Guid.NewGuid();

    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ExpectedEventIds = [eventId1],
      ProcessedEventIds = null  // Not yet populated
    };

    await Assert.That(result.IsFullySynced).IsFalse()
      .Because("ProcessedEventIds is null, can't confirm expected events are processed");
  }

  [Test]
  public async Task SyncInquiryResult_WithProcessedEventIds_StoresIdsAsync() {
    var processedIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ProcessedEventIds = processedIds
    };

    await Assert.That(result.ProcessedEventIds).IsNotNull();
    await Assert.That(result.ProcessedEventIds!.Length).IsEqualTo(2);
    await Assert.That(result.ProcessedEventIds).IsEquivalentTo(processedIds);
  }

  [Test]
  public async Task SyncInquiryResult_WithExpectedEventIds_StoresIdsAsync() {
    var expectedIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var result = new SyncInquiryResult {
      InquiryId = Guid.NewGuid(),
      PendingCount = 0,
      ExpectedEventIds = expectedIds
    };

    await Assert.That(result.ExpectedEventIds).IsNotNull();
    await Assert.That(result.ExpectedEventIds!.Length).IsEqualTo(3);
    await Assert.That(result.ExpectedEventIds).IsEquivalentTo(expectedIds);
  }
}
