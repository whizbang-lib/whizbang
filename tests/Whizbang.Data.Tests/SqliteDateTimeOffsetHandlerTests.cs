using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// The type handler that lets SQLite hold a <see cref="DateTimeOffset"/>.
/// <para>
/// SQLite has no native offset-aware timestamp, so the value is stored as an ISO 8601 string and
/// read back by parsing. The round trip is the contract: an event's timestamp is used to order and
/// filter history, so an offset lost or shifted on the way through changes what a replay sees
/// without anything failing.
/// </para>
/// <para>
/// Parsing is deliberately invariant-culture. A machine with a different locale would otherwise read
/// back a different instant from the same stored bytes — a corruption that only appears on some
/// deployments and is nearly impossible to reproduce on the one that wrote the row.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Sqlite/SqliteDateTimeOffsetHandler.cs</code-under-test>
public class SqliteDateTimeOffsetHandlerTests {

  [Test]
  public async Task AStoredStringRoundTripsToTheSameInstantAsync() {
    var handler = new SqliteDateTimeOffsetHandler();
    var original = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.FromHours(-5));

    var parameter = new SqliteParameter();
    handler.SetValue(parameter, original);
    var readBack = handler.Parse(parameter.Value!);

    await Assert.That(readBack).IsEqualTo(original)
      .Because("an event timestamp orders and filters history, so an offset lost on the way through "
             + "changes what a replay sees without anything failing");
  }

  [Test]
  public async Task AValueThatIsAlreadyATimestamp_IsPassedThroughAsync() {
    // Some providers hand back a typed value rather than a string; re-parsing one would be a
    // needless round trip through text, and text is where offsets get lost.
    var handler = new SqliteDateTimeOffsetHandler();
    var already = DateTimeOffset.UtcNow;

    await Assert.That(handler.Parse(already)).IsEqualTo(already);
  }

  [Test]
  public async Task ParsingIsInvariantOfTheMachineLocaleAsync() {
    // The stored form is written with "O" and must read back identically wherever the row lands.
    var handler = new SqliteDateTimeOffsetHandler();
    var original = new DateTimeOffset(2026, 12, 1, 6, 30, 0, TimeSpan.FromHours(5.5));
    var stored = original.ToString("O", CultureInfo.InvariantCulture);

    await Assert.That(handler.Parse(stored)).IsEqualTo(original)
      .Because("a locale-sensitive parse reads a different instant from the same bytes on a "
             + "different machine, which only shows up on some deployments");
  }

  [Test]
  public async Task AValueItCannotConvert_FailsLoudlyRatherThanGuessingAsync() {
    // Silently returning default here would write an epoch timestamp into event history and make
    // every ordering decision downstream wrong.
    var handler = new SqliteDateTimeOffsetHandler();

    await Assert.That(() => handler.Parse(42))
      .Throws<InvalidCastException>()
      .Because("a defaulted timestamp is worse than a failure — it silently reorders history");
  }
}
