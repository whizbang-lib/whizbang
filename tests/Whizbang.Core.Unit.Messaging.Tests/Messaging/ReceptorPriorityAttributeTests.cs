// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="ReceptorPriorityAttribute"/> (#1412): the priority a receptor declares, which orders the
/// receptors that run for one message. The generator reads the value from source; these read it as the attribute
/// carries it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorPriorityAttribute.cs</code-under-test>
/// <docs>fundamentals/receptors/receptor-priority</docs>
public class ReceptorPriorityAttributeTests {
  [ReceptorPriority(ReceptorPriority.LATE)]
  private sealed class DeclaresLate;

  [Test]
  public async Task Constructor_KeepsThePriorityItIsGivenAsync() {
    var attribute = new ReceptorPriorityAttribute(ReceptorPriority.EARLY);

    await Assert.That(attribute.Priority).IsEqualTo(ReceptorPriority.EARLY);
  }

  [Test]
  public async Task AppliedToAClass_CarriesTheDeclaredPriorityAsync() {
    var attribute = typeof(DeclaresLate).GetCustomAttribute<ReceptorPriorityAttribute>();

    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Priority).IsEqualTo(ReceptorPriority.LATE);
  }
}
