using System;
using System.Linq;
using System.Reflection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Unit tests for <see cref="ReceptorIdempotentAttribute"/> surface: targeting,
/// inheritance, multiple-usage, and the <c>AlwaysFire</c> default.
/// </summary>
public class ReceptorIdempotentAttributeTests {

  [Test]
  public async Task ReceptorIdempotentAttribute_CanBeInstantiatedAsync() {
    var attr = new ReceptorIdempotentAttribute();
    await Assert.That(attr).IsNotNull();
  }

  [Test]
  public async Task ReceptorIdempotentAttribute_AlwaysFire_DefaultsToFalseAsync() {
    var attr = new ReceptorIdempotentAttribute();
    await Assert.That(attr.AlwaysFire).IsFalse()
      .Because("Default semantic: fire only for new events. AlwaysFire must opt-in explicitly.");
  }

  [Test]
  public async Task ReceptorIdempotentAttribute_AlwaysFire_CanBeSetTrueAsync() {
    var attr = new ReceptorIdempotentAttribute { AlwaysFire = true };
    await Assert.That(attr.AlwaysFire).IsTrue();
  }

  [Test]
  public async Task ReceptorIdempotentAttribute_TargetsClassOnlyAsync() {
    var usage = typeof(ReceptorIdempotentAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.Inherited).IsFalse();
    await Assert.That(usage.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task ReceptorIdempotentAttribute_AlwaysFireProperty_IsInitOnlyAsync() {
    // The plan mandates init-only so the attribute is immutable after construction.
    var prop = typeof(ReceptorIdempotentAttribute).GetProperty(nameof(ReceptorIdempotentAttribute.AlwaysFire));
    await Assert.That(prop).IsNotNull();
    var setter = prop!.SetMethod;
    await Assert.That(setter).IsNotNull();
    var setterParams = setter!.ReturnParameter.GetRequiredCustomModifiers();
    var hasIsExternalInit = setterParams.Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    await Assert.That(hasIsExternalInit).IsTrue()
      .Because("AlwaysFire must be init-only so receptor metadata is immutable once declared.");
  }
}
