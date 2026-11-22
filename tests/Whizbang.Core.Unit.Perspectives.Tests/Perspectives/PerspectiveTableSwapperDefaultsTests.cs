// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A table swapper that does not defer indexes (#2248) inherits defaults that defer none and build none, so a staging
/// load on such a driver keeps every index of the shadow table up to date through the load and touches no table.
/// </summary>
public class PerspectiveTableSwapperDefaultsTests {
  private const string SHADOW = "wh_per_test_bg";

  [Test]
  public async Task DeferIndexes_OnASwapperThatDoesNotDefer_DefersNoneAndTouchesNoTableAsync() {
    var swapper = new SwapperWithoutDeferral();

    var deferred = await ((IPerspectiveTableSwapper)swapper).DeferIndexesAsync(SHADOW, CancellationToken.None);

    await Assert.That(deferred).IsEmpty();
    await Assert.That(swapper.Calls).IsEmpty().Because("the default asks nothing of the driver's own table operations");
  }

  [Test]
  public async Task BuildDeferredIndexes_OnASwapperThatDoesNotDefer_CompletesAndTouchesNoTableAsync() {
    var swapper = new SwapperWithoutDeferral();

    var build = ((IPerspectiveTableSwapper)swapper).BuildDeferredIndexesAsync(SHADOW, ["CREATE INDEX ix ON t (x)"], CancellationToken.None);
    await build;

    await Assert.That(build.IsCompletedSuccessfully).IsTrue();
    await Assert.That(swapper.Calls).IsEmpty().Because("with nothing deferred there is nothing to build, whatever it is handed");
  }

  /// <summary>A swapper that implements only the members every driver must, and records each call to them.</summary>
  private sealed class SwapperWithoutDeferral : IPerspectiveTableSwapper {
    public List<string> Calls { get; } = [];

    public Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken) {
      Calls.Add("find");
      return Task.FromResult<string?>(null);
    }

    public Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken) {
      Calls.Add("create");
      return Task.FromResult(liveTable + "_bg");
    }

    public Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) {
      Calls.Add("delete");
      return Task.CompletedTask;
    }

    public Task<string?> SwapAsync(PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken) {
      Calls.Add("swap");
      return Task.FromResult<string?>(null);
    }

    public Task DropAsync(string table, CancellationToken cancellationToken) {
      Calls.Add("drop");
      return Task.CompletedTask;
    }
  }
}
