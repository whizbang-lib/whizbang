// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Which <c>[FireAt]</c> stages make a receptor a lifecycle observer rather than the command's inbox
/// handler, for WHIZ151's ownership claim: every inbox, perspective and post-lifecycle stage does;
/// any other stage, even alongside those, leaves it an inbox handler.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/CompileTimeMessageClassification.cs</code-under-test>
public class CommandOwnershipLifecycleStageTests {
  private static string _twoReceptors(string firstStages, string secondStages) => $$"""
    using Whizbang.Core;
    using Whizbang.Core.Messaging;

    namespace ConsumerApp.Orders.Commands;

    public record CreateOrder(string Name) : ICommand;

    {{firstStages}}
    public class FirstHook : IReceptor<CreateOrder> {
      public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.ValueTask.CompletedTask;
    }

    {{secondStages}}
    public class SecondHook : IReceptor<CreateOrder> {
      public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.ValueTask.CompletedTask;
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[FireAt(LifecycleStage.PrePerspectiveInline)]", "[FireAt(LifecycleStage.PostPerspectiveDetached)]")]
  [Arguments("[FireAt(LifecycleStage.PostAllPerspectivesInline)]", "[FireAt(LifecycleStage.PostLifecycleDetached)]")]
  [Arguments("[FireAt(LifecycleStage.PostInboxInline)]", "[FireAt(LifecycleStage.PreInboxDetached)]")]
  public async Task LifecycleOnlyStages_AreObservers_AndClaimNothingAsync(string first, string second) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(_twoReceptors(first, second));

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnyOtherStage_LeavesTheReceptorAnInboxHandlerAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(_twoReceptors(
      "[FireAt(LifecycleStage.PostLifecycleInline)][FireAt(LifecycleStage.LocalImmediateInline)]",
      "[FireAt(LifecycleStage.PreOutboxInline)]"));

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsNotEmpty()
      .Because("a stage outside the lifecycle set means the receptor handles the command, so two of them compete for it");
  }
}
