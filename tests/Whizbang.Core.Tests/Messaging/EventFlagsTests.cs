#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="EventFlags"/> bitmask shape. The framework
/// dispatch sites check individual flags by bitwise AND; the schema
/// stores the value as a single INTEGER column. Renaming a value or
/// changing its underlying bit position is a breaking change — these
/// tests pin them.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class EventFlagsTests {

  [Test]
  public async Task EventFlags_None_IsZeroAsync() {
    var value = _asInt(EventFlags.None);
    await Assert.That(value).IsEqualTo(0)
      .Because("None must be 0 so the default column value (DEFAULT 0) maps cleanly to 'no flags set' and the bitwise AND with any flag is 0.");
  }

  [Test]
  public async Task EventFlags_Collective_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.Collective);
    await Assert.That(value).IsEqualTo(1)
      .Because("Bit position 0. Production rows already carry this value once Slice 2' lands; renaming or moving it changes the column's meaning silently.");
  }

  [Test]
  public async Task EventFlags_Composite_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.Composite);
    await Assert.That(value).IsEqualTo(2)
      .Because("Bit position 1. Same backward-compat reasoning as Collective.");
  }

  private static int _asInt(EventFlags f) => (int)f;

  [Test]
  public async Task EventFlags_Combine_TwoFlagsAreDistinctBitsAsync() {
    var combined = EventFlags.Collective | EventFlags.Composite;

    await Assert.That(combined.HasFlag(EventFlags.Collective)).IsTrue();
    await Assert.That(combined.HasFlag(EventFlags.Composite)).IsTrue();
    await Assert.That(_asInt(combined)).IsEqualTo(3)
      .Because("OR-ing the two flags gives 0b11 = 3. Catches accidental collision (same bit position for two flags).");
  }

  [Test]
  public async Task EventFlags_HasFlag_NoneAlwaysReturnsTrueForNoneAsync() {
    await Assert.That(EventFlags.None.HasFlag(EventFlags.None)).IsTrue()
      .Because(".NET's HasFlag semantics — every flags value 'has' None. Confirms the test against the .NET behavior the dispatch site relies on.");
  }
}
