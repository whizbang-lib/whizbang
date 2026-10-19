// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.ReceptorPriorityOrder;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1412, against the REAL generated dispatcher: every local receptor for a message runs, on every call,
/// in the order its <see cref="ReceptorPriorityAttribute"/> declares (lower first, the default in the
/// middle). Before the fix a send or local invoke ran only the first receptor for a command while a
/// publish ran them all, in discovery order.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/receptors/receptor-priority</docs>
public class EveryReceptorInPriorityOrderComponentTests {
  public enum Entry {
    SendGeneric,
    SendObject,
    SendWithOptions,
    SendMany,
    LocalSendMany,
    LocalInvokeVoid,
    LocalInvokeVoidObject,
    LocalInvokeResult,
    LocalInvokeResultObject,
  }

  [Test]
  [MatrixDataSource]
  public async Task EveryEntry_RunsEveryLocalReceptorInPriorityOrderAsync([Matrix] Entry entry) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var command = new PrCommand(id);

    switch (entry) {
      case Entry.SendGeneric: await dispatcher.SendAsync(command); break;
      case Entry.SendObject: await dispatcher.SendAsync((object)command); break;
      case Entry.SendWithOptions: await dispatcher.SendAsync((object)command, new DispatchOptions()); break;
      case Entry.SendMany: await dispatcher.SendManyAsync<PrCommand>([command]); break;
      case Entry.LocalSendMany: await dispatcher.LocalSendManyAsync<PrCommand>([command]); break;
      case Entry.LocalInvokeVoid: await dispatcher.LocalInvokeAsync(command); break;
      case Entry.LocalInvokeVoidObject: await dispatcher.LocalInvokeAsync((object)command); break;
      case Entry.LocalInvokeResult: await dispatcher.LocalInvokeAsync<PrCommand, PrResult>(command); break;
      default: await dispatcher.LocalInvokeAsync<PrResult>((object)command); break;
    }

    await Assert.That(PriorityProbe.Ran(id)).IsEquivalentTo(["early", "default", "late"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because($"{entry} runs every local receptor once, lower priority first, an undeclared one at the middle default");
  }

  [Test]
  public async Task Publish_RunsEveryLocalReceptorInPriorityOrderAsync() {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await dispatcher.PublishAsync(new PrEvent(id));

    await Assert.That(PriorityProbe.Ran(id)).IsEquivalentTo(["early", "default", "late"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a publish runs its receptors in the same declared order");
  }

  [Test]
  public async Task LocalInvokeForAResult_ReturnsTheEarliestAnsweringReceptorsResult_AndRunsTheRestAsync() {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    var result = await dispatcher.LocalInvokeAsync<PrTwoAnswersCommand, PrResult>(new PrTwoAnswersCommand(id));

    await Assert.That(result.AnsweredBy).IsEqualTo("early")
      .Because("when several receptors answer, the earliest by priority answers the caller");
    await Assert.That(PriorityProbe.Ran(id)).IsEquivalentTo(["early", "late"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the other answering receptor still runs");
  }

  private static ServiceProvider _buildProvider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
