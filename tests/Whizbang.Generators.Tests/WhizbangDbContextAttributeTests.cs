using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Locks the property contract of <see cref="WhizbangDbContextAttribute"/>.
/// The attribute drives EF Core source-generator discovery — its Keys
/// initializer (default empty-string fallback) and optional Schema /
/// ConnectionStringName properties are what generator authors actually
/// read. Without these tests the whole Whizbang.Data.EFCore.Custom
/// assembly sat at 0% coverage.
/// </summary>
public class WhizbangDbContextAttributeTests {

  [Test]
  public async Task NoKeys_DefaultsToEmptyStringKeyAsync() {
    var attr = new WhizbangDbContextAttribute();
    await Assert.That(attr.Keys.Length).IsEqualTo(1);
    await Assert.That(attr.Keys[0]).IsEqualTo("");
  }

  [Test]
  public async Task NullKeys_DefaultsToEmptyStringKeyAsync() {
    var attr = new WhizbangDbContextAttribute(null);
    await Assert.That(attr.Keys.Length).IsEqualTo(1);
    await Assert.That(attr.Keys[0]).IsEqualTo("");
  }

  [Test]
  public async Task SingleKey_PreservedVerbatimAsync() {
    var attr = new WhizbangDbContextAttribute("catalog");
    await Assert.That(attr.Keys.Length).IsEqualTo(1);
    await Assert.That(attr.Keys[0]).IsEqualTo("catalog");
  }

  [Test]
  public async Task MultipleKeys_PreservedInOrderAsync() {
    var attr = new WhizbangDbContextAttribute("catalog", "shared", "orders");
    await Assert.That(attr.Keys.Length).IsEqualTo(3);
    await Assert.That(attr.Keys[0]).IsEqualTo("catalog");
    await Assert.That(attr.Keys[1]).IsEqualTo("shared");
    await Assert.That(attr.Keys[2]).IsEqualTo("orders");
  }

  [Test]
  public async Task Schema_DefaultIsNullAsync() {
    var attr = new WhizbangDbContextAttribute();
    await Assert.That(attr.Schema).IsNull();
  }

  [Test]
  public async Task Schema_RoundTripsAsync() {
    var attr = new WhizbangDbContextAttribute { Schema = "inventory" };
    await Assert.That(attr.Schema).IsEqualTo("inventory");
  }

  [Test]
  public async Task ConnectionStringName_DefaultIsNullAsync() {
    var attr = new WhizbangDbContextAttribute();
    await Assert.That(attr.ConnectionStringName).IsNull();
  }

  [Test]
  public async Task ConnectionStringName_RoundTripsAsync() {
    var attr = new WhizbangDbContextAttribute { ConnectionStringName = "chat-service-db" };
    await Assert.That(attr.ConnectionStringName).IsEqualTo("chat-service-db");
  }
}
