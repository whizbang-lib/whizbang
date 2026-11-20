// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2314: the generated perspective registry names the event types whose Apply can only purge the row, in the event
/// store's form, so the purge-marker backfill can tell a row it finds missing was purged. It uses the runner's own
/// terminal-purge rule (#1151): a conditional purge is not listed, because a wrong marker skips the stream's later events.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs</code-under-test>
[Category("Generators")]
public class PerspectiveRegistryUnconditionalPurgeTests {
  private const string TEMPLATE = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OpenedEvent : IEvent {
    [StreamId] public Guid Id { get; init; }
  }

  public record ClosedEvent : IEvent {
    [StreamId] public Guid Id { get; init; }
    public bool Hard { get; init; }
  }

  public record AccountModel {
    [StreamId] public Guid Id { get; init; }
    public string Status { get; init; } = "";
  }

  public class AccountPerspective :
      IPerspectiveFor<AccountModel, OpenedEvent>,
      IPerspectiveWithActionsFor<AccountModel, ClosedEvent> {
    public AccountModel Apply(AccountModel currentData, OpenedEvent @event) => currentData with { Status = "open" };

    __CLOSED_APPLY__
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Registry_AnApplyThatCanOnlyPurge_ListsItsEventTypeAsync() {
    var entry = _registryEntry("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => ApplyResult<AccountModel>.Purge();");

    await Assert.That(entry).Contains("UnconditionalPurgeEventTypes = [\"TestNamespace.ClosedEvent, ");
    await Assert.That(entry).DoesNotContain("TestNamespace.OpenedEvent, TestAssembly\"]")
      .Because("an Apply that returns a model is not a purge");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Registry_AConditionalPurge_IsNotListedAsync() {
    var entry = _registryEntry("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => @event.Hard ? ApplyResult<AccountModel>.Purge() : ApplyResult<AccountModel>.Update(currentData);");

    await Assert.That(entry).DoesNotContain("UnconditionalPurgeEventTypes")
      .Because("a row missing after a conditional purge may never have been purged");
  }

  private static string _registryEntry(string closedApply) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(TEMPLATE.Replace("__CLOSED_APPLY__", closedApply));
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs")!;
    var start = registry.IndexOf("\"TestNamespace.AccountPerspective\",", StringComparison.Ordinal);
    return registry[start..registry.IndexOf("];", start, StringComparison.Ordinal)];
  }
}
