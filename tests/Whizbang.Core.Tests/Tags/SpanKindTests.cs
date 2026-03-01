using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="SpanKind"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Tags/SpanKind.cs</tests>
public class SpanKindTests {
  [Test]
  public async Task SpanKind_Internal_IsDefinedAsync() {
    var value = SpanKind.Internal;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SpanKind_Server_IsDefinedAsync() {
    var value = SpanKind.Server;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SpanKind_Client_IsDefinedAsync() {
    var value = SpanKind.Client;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SpanKind_Producer_IsDefinedAsync() {
    var value = SpanKind.Producer;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SpanKind_Consumer_IsDefinedAsync() {
    var value = SpanKind.Consumer;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SpanKind_HasFiveValuesAsync() {
    var values = Enum.GetValues<SpanKind>();
    await Assert.That(values.Length).IsEqualTo(5);
  }

  [Test]
  public async Task SpanKind_Internal_HasCorrectIntValueAsync() {
    var value = (int)SpanKind.Internal;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task SpanKind_Server_HasCorrectIntValueAsync() {
    var value = (int)SpanKind.Server;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task SpanKind_Client_HasCorrectIntValueAsync() {
    var value = (int)SpanKind.Client;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task SpanKind_Producer_HasCorrectIntValueAsync() {
    var value = (int)SpanKind.Producer;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task SpanKind_Consumer_HasCorrectIntValueAsync() {
    var value = (int)SpanKind.Consumer;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task SpanKind_Internal_IsDefaultAsync() {
    var value = default(SpanKind);
    await Assert.That(value).IsEqualTo(SpanKind.Internal);
  }
}
