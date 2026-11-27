// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Whizbang.Testing.Async;

/// <summary>
/// Whether an object a test made and dropped is still kept alive by something else: a channel's waiter queue, a token's
/// callback list, a task's continuations. The check a counting wrapper cannot make, because what keeps the object is
/// not a seam the test can hand in.
/// </summary>
/// <remarks>
/// <para>
/// <c>abandon</c> runs in a method of its own that is never inlined, so nothing on the test's stack keeps what it
/// returns alive; a full collection then decides. Whatever is still alive afterwards is kept by something reachable
/// from a root, typically the long-lived object the code under test registered it with.
/// </para>
/// <para>
/// Release must be complete when <c>abandon</c> returns. A completion that finishes on the thread pool can keep the
/// object reachable for a moment from a queued work item, which reads as kept: make the release synchronous (an
/// unbounded channel created with <c>AllowSynchronousContinuations</c>, a cancel rather than a cancel-async), or wait
/// on the work's own signal before checking. A check that expects release needs a positive control that expects the
/// opposite under the same helpers, so a collector that never ran cannot pass it.
/// </para>
/// </remarks>
/// <docs>contributors/testing/loop-waits</docs>
/// <tests>tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs</tests>
public static class ReleaseCheck {
  /// <summary>True when the object <paramref name="abandon"/> made and dropped is collected by a full collection.</summary>
  /// <param name="abandon">Makes the object, hands it to the code under test, drops every reference of its own, and returns it.</param>
  public static bool IsReleased(Func<object> abandon) {
    ArgumentNullException.ThrowIfNull(abandon);
    var weak = _abandon(abandon);
    Collect();
    return !weak.IsAlive;
  }

  /// <summary>A full, blocking collection with finalizers run, twice so objects a finalizer freed are collected too.</summary>
  [SuppressMessage("Major Code Smell", "S1215:\"GC.Collect\" should not be called", Justification = "A release check is decided by a full collection: whether anything still keeps the object is exactly what it asks.")]
  public static void Collect() {
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference _abandon(Func<object> abandon) => new(abandon());
}
