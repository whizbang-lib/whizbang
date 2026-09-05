using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Tests;

/// <summary>
/// The Dapper handler that reads a <see cref="TrackedGuid"/> back out of a uuid column.
/// <para>
/// The read goes through <c>FromExternal</c> because a value coming off a row was minted somewhere
/// else, possibly by another service and possibly long ago. Treating it as freshly generated would
/// assert a provenance the row never carried, and that provenance is what distinguishes an
/// identifier this process created from one it merely observed.
/// </para>
/// <para>
/// An unconvertible value throws rather than defaulting. A default identifier is far worse than a
/// failure here: it is a valid-looking key that silently collides with every other defaulted row.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Custom/TrackedGuidHandler.cs</code-under-test>
public class TrackedGuidHandlerTests {

  [Test]
  public async Task AGuidFromARow_ReadsBackAsAnExternalIdentifierAsync() {
    var handler = new TrackedGuidHandler();
    var fromRow = Guid.CreateVersion7();

    var parsed = handler.Parse(fromRow);

    await Assert.That(parsed.Value).IsEqualTo(fromRow);
    await Assert.That(parsed).IsEqualTo(TrackedGuid.FromExternal(fromRow))
      .Because("a row's identifier was minted elsewhere, so claiming this process generated it "
             + "asserts a provenance the row never carried");
  }

  [Test]
  public async Task AValueItCannotConvert_FailsRatherThanDefaultingAsync() {
    var handler = new TrackedGuidHandler();

    await Assert.That(() => handler.Parse("not-a-guid"))
      .Throws<InvalidCastException>()
      .Because("a defaulted identifier is a valid-looking key that silently collides with every "
             + "other defaulted row, which is worse than a loud failure");
  }
}
