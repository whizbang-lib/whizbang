using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707 // test method names use underscores
#pragma warning disable IDE1006 // test method names

public class RawReceptorRegistryTests {

  private sealed class FakeRawReceptor(string targetTypeName) : IRawReceptor {
    public string TargetMessageTypeName { get; } = targetTypeName;
    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  [Test]
  public async Task FindByTypeName_RegisteredReceptor_ReturnsItAsync() {
    var receptor = new FakeRawReceptor("MyApp.Events.WidgetCreated, MyApp.Contracts");
    var registry = new RawReceptorRegistry([receptor]);

    var found = registry.FindByTypeName("MyApp.Events.WidgetCreated, MyApp.Contracts");

    await Assert.That(found).IsSameReferenceAs(receptor);
  }

  [Test]
  public async Task FindByTypeName_VersionDifferent_StillResolvesAsync() {
    // EventTypeMatchingHelper.NormalizeTypeName strips Version/Culture/PublicKeyToken so
    // contracts assembly version drift doesn't break raw receptor lookup.
    var receptor = new FakeRawReceptor("MyApp.Events.WidgetCreated, MyApp.Contracts");
    var registry = new RawReceptorRegistry([receptor]);

    var found = registry.FindByTypeName(
      "MyApp.Events.WidgetCreated, MyApp.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null");

    await Assert.That(found).IsSameReferenceAs(receptor);
  }

  [Test]
  public async Task FindByTypeName_NotRegistered_ReturnsNullAsync() {
    var registry = new RawReceptorRegistry([new FakeRawReceptor("MyApp.A, MyApp")]);

    var found = registry.FindByTypeName("MyApp.B, MyApp");

    await Assert.That(found).IsNull();
  }

  [Test]
  public async Task RegisteredTypeNames_ReflectsAllRegisteredReceptorsAsync() {
    var registry = new RawReceptorRegistry([
      new FakeRawReceptor("MyApp.A, MyApp"),
      new FakeRawReceptor("MyApp.B, MyApp"),
    ]);

    await Assert.That(registry.RegisteredTypeNames).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Constructor_DuplicateTargetTypeName_ThrowsAsync() {
    // Two raw receptors competing for the same type name is a configuration bug — fail
    // loud at startup rather than silently picking one.
    await Assert.That(() => new RawReceptorRegistry([
      new FakeRawReceptor("MyApp.A, MyApp"),
      new FakeRawReceptor("MyApp.A, MyApp"),
    ])).Throws<InvalidOperationException>();
  }
}
