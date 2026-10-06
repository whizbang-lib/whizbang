// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The runner knows which event types always purge the row (#1151), so a rebuild or rewind whose last event is one of
/// them deletes the row instead of folding the whole stream first. Only an Apply whose body can do nothing but purge
/// counts: a conditional purge is replayed, because a wrong call deletes a row that should have survived.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#terminal-purges</docs>
[Category("Generators")]
public class PerspectiveRunnerTerminalPurgeTests {
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

  private const string PURGE_SET = "=> @event is global::TestNamespace.ClosedEvent";

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => ApplyResult<AccountModel>.Purge();")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) { return ApplyResult<AccountModel>.Purge(); }")]
  [Arguments("public ModelAction Apply(AccountModel currentData, ClosedEvent @event) => ModelAction.Purge;")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (null, ModelAction.Purge);")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => (ApplyResult<AccountModel>.Purge());")]
  [Arguments("public ModelAction Apply(AccountModel currentData, ClosedEvent @event) => Whizbang.Core.Perspectives.ModelAction.Purge;")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => global::Whizbang.Core.Perspectives.ApplyResult<AccountModel>.Purge();")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (default, ModelAction.Purge);")]
  public async Task AnApplyThatCanOnlyPurge_IsATerminalPurgeAsync(string closedApply) {
    var runner = _runnerFor(closedApply);

    await Assert.That(runner).Contains(PURGE_SET);
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => @event.Hard ? ApplyResult<AccountModel>.Purge() : ApplyResult<AccountModel>.Update(currentData);")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) { if (@event.Hard) { return ApplyResult<AccountModel>.Purge(); } return ApplyResult<AccountModel>.None(); }")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) { Console.WriteLine(); return ApplyResult<AccountModel>.Purge(); }")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => ApplyResult<AccountModel>.Delete();")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => ApplyResult<AccountModel>.Update(currentData);")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => Decide();")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => Decide().Purge();")]
  [Arguments("public ApplyResult<AccountModel> Apply(AccountModel currentData, ClosedEvent @event) => Outcomes.Purge();")]
  [Arguments("public AccountModel Apply(AccountModel currentData, ClosedEvent @event) => currentData;")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (currentData, ModelAction.Purge);")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (0, ModelAction.Purge);")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (null, @event.Hard ? ModelAction.Purge : ModelAction.None);")]
  [Arguments("public (AccountModel?, ModelAction) Apply(AccountModel currentData, ClosedEvent @event) => (null, ModelAction.Delete);")]
  [Arguments("public (AccountModel?, ModelAction, int) Apply(AccountModel currentData, ClosedEvent @event) => (null, ModelAction.Purge, 1);")]
  public async Task AnApplyThatMightNotPurge_IsReplayedAsync(string closedApply) {
    var runner = _runnerFor(closedApply);

    await Assert.That(runner).DoesNotContain(PURGE_SET);
    await Assert.That(runner).Contains("IsUnconditionalPurge(global::Whizbang.Core.IEvent @event) => false;");
  }

  private static string _runnerFor(string closedApply) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(TEMPLATE.Replace("__CLOSED_APPLY__", closedApply));
    return GeneratorTestHelper.GetGeneratedSource(result, "AccountPerspectiveRunner.g.cs")!;
  }
}
