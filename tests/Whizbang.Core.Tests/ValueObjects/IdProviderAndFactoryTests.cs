using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Covers the source-generated per-Id {Id}Provider + {Id}Factory classes
/// that the WhizbangIdGenerator emits. Each Id type gets two trivial
/// wrappers around IWhizbangIdProvider / .New(); none were touched by
/// the existing tests, so each one sat at 0% coverage. Asserting null
/// guard + return-type-and-uniqueness gives the generator emit full
/// coverage parity.
/// </summary>
public class IdProviderAndFactoryTests {

  private sealed class FakeIdProvider : IWhizbangIdProvider {
    public TrackedGuid NewGuid() => TrackedGuid.NewMedo();
  }

  [Test]
  public async Task MessageIdProvider_NewId_ProducesUniqueIdsAsync() {
    var sut = new MessageIdProvider(new FakeIdProvider());
    var a = sut.NewId();
    var b = sut.NewId();
    await Assert.That(a == b).IsFalse();
  }

  [Test]
  public async Task MessageIdProvider_NullBaseProvider_ThrowsAsync() {
    await Assert.That(() => new MessageIdProvider(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task MessageIdFactory_Create_ProducesUniqueIdsAsync() {
    var sut = new MessageIdFactory();
    var a = sut.Create();
    var b = sut.Create();
    await Assert.That(a == b).IsFalse();
  }

  [Test]
  public async Task StreamIdProvider_NewId_ProducesUniqueIdsAsync() {
    var sut = new StreamIdProvider(new FakeIdProvider());
    var a = sut.NewId();
    var b = sut.NewId();
    await Assert.That(a == b).IsFalse();
  }

  [Test]
  public async Task StreamIdProvider_NullBaseProvider_ThrowsAsync() {
    await Assert.That(() => new StreamIdProvider(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task StreamIdFactory_Create_ProducesUniqueIdsAsync() {
    var sut = new StreamIdFactory();
    var a = sut.Create();
    var b = sut.Create();
    await Assert.That(a == b).IsFalse();
  }

  [Test]
  public async Task EventIdProvider_NewId_ProducesUniqueIdsAsync() {
    var sut = new EventIdProvider(new FakeIdProvider());
    var a = sut.NewId();
    var b = sut.NewId();
    await Assert.That(a == b).IsFalse();
  }

  [Test]
  public async Task EventIdProvider_NullBaseProvider_ThrowsAsync() {
    await Assert.That(() => new EventIdProvider(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CorrelationIdFactory_Create_ProducesUniqueIdsAsync() {
    var sut = new CorrelationIdFactory();
    var a = sut.Create();
    var b = sut.Create();
    await Assert.That(a == b).IsFalse();
  }
}
