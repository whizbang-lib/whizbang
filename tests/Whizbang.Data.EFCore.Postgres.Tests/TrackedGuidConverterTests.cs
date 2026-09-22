using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The value converter that lets a <see cref="TrackedGuid"/> live in a plain uuid column.
/// <para>
/// Both directions had to be pinned, and the round trip is the point: the identifier written to a
/// row and the one materialized back out have to be the same value, because it is the key other
/// rows reference. A converter that altered the value in either direction would produce rows that
/// no longer join, which surfaces far from the conversion that caused it.
/// </para>
/// <para>
/// The read direction goes through <c>FromExternal</c> on purpose. A value loaded from the database
/// was not minted in this process, so its provenance is inferred rather than known — treating it as
/// freshly generated would claim knowledge of a timestamp and origin the row never carried.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/TrackedGuidConverter.cs</code-under-test>
[Category("Shard3")]
public class TrackedGuidConverterTests {

  [Test]
  public async Task WritingThenReading_YieldsTheSameIdentifierAsync() {
    var converter = new TrackedGuidConverter();
    var original = TrackedGuid.NewMedo();

    var stored = (Guid)converter.ConvertToProvider(original)!;
    var loaded = (TrackedGuid)converter.ConvertFromProvider(stored)!;

    await Assert.That(stored).IsEqualTo(original.Value)
      .Because("the column holds the raw value; altering it on the way down breaks every row that "
             + "references this key");
    await Assert.That(loaded.Value).IsEqualTo(original.Value)
      .Because("a round trip that does not return the same identifier produces rows that no longer "
             + "join, and the damage shows up far from the conversion");
  }

  [Test]
  public async Task AValueLoadedFromTheDatabase_IsTreatedAsExternalRatherThanFreshAsync() {
    // Provenance matters: a row's identifier was minted somewhere else, possibly long ago. Marking
    // it as generated here would assert a timestamp and origin the row never carried.
    var converter = new TrackedGuidConverter();
    var fromRow = Guid.CreateVersion7();

    var loaded = (TrackedGuid)converter.ConvertFromProvider(fromRow)!;

    await Assert.That(loaded.Value).IsEqualTo(fromRow);
    await Assert.That(loaded).IsEqualTo(TrackedGuid.FromExternal(fromRow))
      .Because("the read path must agree with FromExternal, or the same row read twice through "
             + "different paths describes its own origin differently");
  }
}
