using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Utilities;

#pragma warning disable CA1707 // test method names use underscores
#pragma warning disable IDE1006 // test method names

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for AttributeArgNamingHelper — converts an attribute constructor parameter name
/// to its corresponding property-initializer name using a configurable
/// <see cref="AttributeArgNamingConvention"/>. Used by MessageTagDiscoveryGenerator to
/// emit positional-ctor-arg values into the AttributeFactory's object initializer.
/// </summary>
public class AttributeArgNamingHelperTests {

  [Test]
  public async Task PascalCase_LowerCamelInput_CapitalizesFirstAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.PascalCase))
      .IsEqualTo("TagValue");
  }

  [Test]
  public async Task PascalCase_AlreadyPascal_UnchangedAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("TagValue", AttributeArgNamingConvention.PascalCase))
      .IsEqualTo("TagValue");
  }

  [Test]
  public async Task PascalCase_SingleLetter_CapitalizedAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("x", AttributeArgNamingConvention.PascalCase)).IsEqualTo("X");
  }

  [Test]
  public async Task PascalCase_Empty_ReturnsEmptyAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("", AttributeArgNamingConvention.PascalCase)).IsEqualTo("");
  }

  [Test]
  public async Task Identity_AnyInput_UnchangedAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.Identity)).IsEqualTo("tagValue");
    await Assert.That(AttributeArgNamingHelper.Convert("TagValue", AttributeArgNamingConvention.Identity)).IsEqualTo("TagValue");
  }

  [Test]
  public async Task CamelCase_PascalInput_LowercasesFirstAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("TagValue", AttributeArgNamingConvention.CamelCase))
      .IsEqualTo("tagValue");
  }

  [Test]
  public async Task CamelCase_AlreadyCamel_UnchangedAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.CamelCase))
      .IsEqualTo("tagValue");
  }

  [Test]
  public async Task SnakeCase_CamelInput_AddsUnderscoresAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.SnakeCase))
      .IsEqualTo("tag_value");
  }

  [Test]
  public async Task SnakeCase_PascalInput_AddsUnderscoresAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("TagValue", AttributeArgNamingConvention.SnakeCase))
      .IsEqualTo("tag_value");
  }

  [Test]
  public async Task SnakeCase_MultipleBoundaries_HandledAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("HTTPRequestHandler", AttributeArgNamingConvention.SnakeCase))
      .IsEqualTo("http_request_handler");
  }

  [Test]
  public async Task KebabCase_CamelInput_AddsHyphensAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.KebabCase))
      .IsEqualTo("tag-value");
  }

  [Test]
  public async Task UpperSnake_CamelInput_UppercasedAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("tagValue", AttributeArgNamingConvention.UpperSnake))
      .IsEqualTo("TAG_VALUE");
  }
}
