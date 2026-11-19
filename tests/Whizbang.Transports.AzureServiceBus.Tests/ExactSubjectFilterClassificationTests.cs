// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// #2089: a foreign subscription on a command inbox is legitimate only when its SQL filter is exact subject matches.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceBusInfrastructureProvisioner.cs</code-under-test>
public class ExactSubjectFilterClassificationTests {
  [Test]
  [Arguments("sys.Label LIKE 'myapp.orders.commands.exportorders'")]
  [Arguments("sys.Label LIKE 'a.b.c' OR sys.Label LIKE 'a.b.d+e'")]
  public async Task ExactSubjects_AreExactAsync(string expression) {
    await Assert.That(ServiceBusInfrastructureProvisioner.IsExactSubjectExpression(expression)).IsTrue();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("1=1")]
  [Arguments("sys.Label LIKE 'myapp.orders.%'")]
  [Arguments("sys.Label LIKE 'myapp.orders.command_x'")]
  [Arguments("sys.Label LIKE ''")]
  [Arguments("sys.Label LIKE 'unterminated")]
  [Arguments("sys.Label LIKE 'a' OR 1=1")]
  [Arguments("sys.Label LIKE 'a'' OR ''b'")]
  public async Task AnythingElse_IsNotExactAsync(string? expression) {
    await Assert.That(ServiceBusInfrastructureProvisioner.IsExactSubjectExpression(expression)).IsFalse();
  }
}
