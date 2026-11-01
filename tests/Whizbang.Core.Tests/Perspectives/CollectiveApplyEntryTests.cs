// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The key the collective sink records an apply's progress under (#1969): stable across restarts, and distinct for every
/// handler method that applies an event to a model.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveApplyEntry.cs</code-under-test>
public class CollectiveApplyEntryTests {
  private sealed class Handler;

  private sealed class Model;

  private static CollectiveApplyEntry _entry(string method) => new(
    ModelType: typeof(Model),
    EventType: typeof(object),
    HandlerType: typeof(Handler),
    MethodName: method,
    ScopeHandling: default,
    SpecKind: default,
    Invoker: static (_, _, _) => new object());

  [Test]
  public async Task EntryKey_NamesTheHandlerTheMethodAndTheModelAsync() {
    await Assert.That(_entry("Archive").EntryKey).IsEqualTo(
      "Whizbang.Core.Tests.Perspectives.CollectiveApplyEntryTests+Handler.Archive:"
      + "Whizbang.Core.Tests.Perspectives.CollectiveApplyEntryTests+Model")
      .Because("the key is built from names only, so it is the same after a restart or a redeploy");
  }

  [Test]
  public async Task EntryKey_DiffersForTwoMethodsOfOneHandlerAsync() {
    await Assert.That(_entry("Archive").EntryKey).IsNotEqualTo(_entry("Review").EntryKey)
      .Because("two handlers of one event and one model each keep their own progress");
  }

  [Test]
  public async Task EntryKey_IsKeptByACopyWithAnotherInvokerAsync() {
    var entry = _entry("Archive");
    var wrapped = entry with { Invoker = static (_, _, _) => new object() };

    await Assert.That(wrapped.EntryKey).IsEqualTo(entry.EntryKey)
      .Because("the dispatcher hands the executor a copy whose invoker records the changed properties");
  }
}
