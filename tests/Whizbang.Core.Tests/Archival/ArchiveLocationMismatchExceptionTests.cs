// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The refusal an archive store throws when a record's location is not the configured one (#2137): it names the stream,
/// the operation, both locations and the record's location id, so an operator can tell a copied or restored database
/// from a misconfiguration and knows which row to repoint.
/// </summary>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
public class ArchiveLocationMismatchExceptionTests {
  private static readonly Guid _stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

  private static readonly ArchiveLocation _recorded =
    new("azure-blob", "https://source.blob.core.windows.net", "archive", "archive-source");

  private static readonly ArchiveLocation _configured =
    new("azure-blob", "https://copy.blob.core.windows.net", "archive", "archive-copy");

  [Test]
  public async Task TheLocationsConstructor_NamesTheStreamTheOperationBothLocationsAndTheRowAsync() {
    var ex = new ArchiveLocationMismatchException(_stream, "delete", 7, _recorded, _configured);

    await Assert.That(ex.StreamId).IsEqualTo(_stream);
    await Assert.That(ex.Operation).IsEqualTo("delete");
    await Assert.That(ex.RecordedLocationId).IsEqualTo(7L);
    await Assert.That(ex.Recorded).IsEqualTo(_recorded);
    await Assert.That(ex.Configured).IsEqualTo(_configured);
    await Assert.That(ex.Message)
      .Contains($"delete of the archive of stream {_stream}")
      .And.Contains("location 7")
      .And.Contains("https://source.blob.core.windows.net")
      .And.Contains("archive-source")
      .And.Contains("https://copy.blob.core.windows.net")
      .And.Contains("archive-copy")
      .And.Contains("Nothing was read, restored or deleted")
      .And.Contains("wh_archive_locations");
  }

  [Test]
  public async Task ARecordWithNoLocation_SaysSo_RatherThanNamingOneAsync() {
    var ex = new ArchiveLocationMismatchException(_stream, "read", recordedLocationId: null, recorded: null, _configured);

    await Assert.That(ex.RecordedLocationId).IsNull();
    await Assert.That(ex.Recorded).IsNull();
    await Assert.That(ex.Message).Contains("records no location").And.Contains("archive-copy");
  }

  [Test]
  public async Task ALocationRecordedBeforeEndpointsWere_ReadsAsNotRecordedAsync() {
    var legacy = new ArchiveLocation("azure-blob", Endpoint: null, ConnectionName: null, "archive-source");

    await Assert.That(legacy.ToString())
      .IsEqualTo("azure-blob, endpoint (not recorded), connection (not recorded), container 'archive-source'");
    await Assert.That(_recorded.ToString())
      .IsEqualTo("azure-blob, endpoint https://source.blob.core.windows.net, connection 'archive', container 'archive-source'");
  }

  [Test]
  public async Task TheStandardConstructors_LeaveTheDetailsUnknownAsync() {
    var inner = new InvalidOperationException("cause");

    var bare = new ArchiveLocationMismatchException();
    var withMessage = new ArchiveLocationMismatchException("mismatch");
    var withCause = new ArchiveLocationMismatchException("mismatch", inner);

    await Assert.That(bare.StreamId).IsNull();
    await Assert.That(bare.Operation).IsNull();
    await Assert.That(bare.Configured).IsNull();
    await Assert.That(withMessage.Message).IsEqualTo("mismatch");
    await Assert.That(withMessage.Recorded).IsNull();
    await Assert.That(withCause.InnerException).IsSameReferenceAs(inner);
    await Assert.That(withCause.RecordedLocationId).IsNull();
  }
}
