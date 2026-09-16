using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// That the streams whose stored form could not be read are remembered once each, until they
/// read again, and forgotten when nothing has been heard of them for long enough.
/// </summary>
/// <remarks>
/// The registry is what makes the worker's error fire once per perspective and stream instead of
/// once per drain cycle, and what the health endpoint counts. It is in-process and bounded: a
/// stream that reads again is released, and one that is never heard of again is swept.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/StoredFormFailureRegistry.cs</code-under-test>
[Category("Core")]
[Category("Perspectives")]
public class StoredFormFailureRegistryTests {
  private static readonly DateTimeOffset _start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

  private static StoredFormUnreadable _failure() {
    try {
      JsonSerializer.Deserialize("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      StoredFormUnreadable.TryClassify(ex, out var failure);
      return failure!;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  /// <summary>The first failure for a stream is the first; the next is not.</summary>
  [Test]
  public async Task TheFirstFailureForAStreamIsToldFromTheNextAsync() {
    var registry = new StoredFormFailureRegistry(new FakeTimeProvider(_start));
    var stream = Guid.CreateVersion7();

    var first = registry.Record("Orders", stream, _failure());
    var second = registry.Record("Orders", stream, _failure());
    var other = registry.Record("Orders", Guid.CreateVersion7(), _failure());

    await Assert.That(first.Failures).IsEqualTo(1);
    await Assert.That(second.Failures).IsEqualTo(2);
    await Assert.That(other.Failures).IsEqualTo(1)
      .Because("the announcement is per perspective and stream, not per perspective");
    await Assert.That(first.Path).IsEqualTo("$.At");
    await Assert.That(first.Detail).Contains("True", StringComparison.Ordinal);
    await Assert.That(registry.Count).IsEqualTo(2);
  }

  /// <summary>A stream that reads again is released, and a later failure is a first again.</summary>
  [Test]
  public async Task AStreamThatReadsAgainIsReleasedAsync() {
    var registry = new StoredFormFailureRegistry(new FakeTimeProvider(_start));
    var stream = Guid.CreateVersion7();
    registry.Record("Orders", stream, _failure());

    var released = registry.Recovered("Orders", stream);
    var again = registry.Recovered("Orders", stream);

    await Assert.That(released).IsTrue();
    await Assert.That(again).IsFalse();
    await Assert.That(registry.Count).IsEqualTo(0);
    await Assert.That(registry.Record("Orders", stream, _failure()).Failures).IsEqualTo(1)
      .Because("a stream that recovered and failed again is a new episode, announced again");
  }

  /// <summary>A stream nothing has been heard of for long enough is forgotten.</summary>
  [Test]
  public async Task AStreamNotHeardOfForLongEnoughIsForgottenAsync() {
    var time = new FakeTimeProvider(_start);
    var registry = new StoredFormFailureRegistry(time) { ForgetAfter = TimeSpan.FromMinutes(30) };
    var stale = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    registry.Record("Orders", stale, _failure());
    time.Advance(TimeSpan.FromMinutes(20));
    registry.Record("Orders", fresh, _failure());
    time.Advance(TimeSpan.FromMinutes(11));

    var snapshot = registry.Snapshot();

    await Assert.That(snapshot.Select(e => e.StreamId)).IsEquivalentTo([fresh])
      .Because("a stream whose rows were dead-lettered is never heard of again, and a registry that "
        + "kept it would grow with every such stream for the life of the process");
    await Assert.That(registry.Count).IsEqualTo(1);
  }

  /// <summary>The snapshot carries what the health endpoint reports.</summary>
  [Test]
  public async Task TheSnapshotCarriesTheDetailAsync() {
    var time = new FakeTimeProvider(_start);
    var registry = new StoredFormFailureRegistry(time);
    var stream = Guid.CreateVersion7();
    registry.Record("Orders", stream, _failure());

    var entry = registry.Snapshot().Single();

    await Assert.That(entry.PerspectiveName).IsEqualTo("Orders");
    await Assert.That(entry.StreamId).IsEqualTo(stream);
    await Assert.That(entry.LastFailedAt).IsEqualTo(_start);
  }
}
