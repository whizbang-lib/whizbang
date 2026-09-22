using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.AutoPopulate;

[Category("Core")]
[Category("Attributes")]
[Category("AutoPopulate")]
public class PopulateFromHttpHeaderAttributeTests {
  [Test]
  public async Task Constructor_WithHeaderName_SetsHeaderNameAsync() {
    var attribute = new PopulateFromHttpHeaderAttribute("X-Tenant-Id");

    await Assert.That(attribute.HeaderName).IsEqualTo("X-Tenant-Id");
  }

  [Test]
  public async Task AttributeUsage_TargetsPropertiesAndParametersAsync() {
    var usage = typeof(PopulateFromHttpHeaderAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsTrue();
  }

  [Test]
  public async Task AppliedToRecordProperty_IsDiscoverableAsync() {
    var property = typeof(TestRequestWithHeader).GetProperty(nameof(TestRequestWithHeader.TenantId));

    await Assert.That(property).IsNotNull();

    var attribute = property!
        .GetCustomAttributes(typeof(PopulateFromHttpHeaderAttribute), inherit: true)
        .Cast<PopulateFromHttpHeaderAttribute>()
        .SingleOrDefault();

    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.HeaderName).IsEqualTo("X-Tenant-Id");
  }

  private sealed record TestRequestWithHeader(
      Guid Id,
      [property: PopulateFromHttpHeader("X-Tenant-Id")] string? TenantId = null
  );
}
