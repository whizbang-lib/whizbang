// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Tests.Common;

/// <summary>
/// Shared constants for test assertions.
/// Update these when intentionally adding/removing test receptors.
/// </summary>
public static class TestConstants {
  /// <summary>
  /// <para>Expected number of receptors the generator discovers in this assembly. Each Core unit test project has its own
  /// generated registry, so this counts only the receptors compiled into Whizbang.Core.Tests; the dispatcher, messaging,
  /// perspective and worker tests, with most of the receptors, moved to the Whizbang.Core.Unit.*.Tests projects (#2392).</para>
  ///
  /// <para>- 5 receptors from Receptors/ReceptorTests.cs
  /// - 3 receptors from Receptors/SyncReceptorTests.cs
  /// - 3 receptors from Receptors/VoidReceptorTests.cs
  /// - 4 receptors from Dispatcher/DispatcherSyncTests.cs
  /// - 7 receptors from Examples/VoidReceptorExamples.cs</para>
  ///
  /// <para>Total: 22 receptors</para>
  /// </summary>
  public const int EXPECTED_RECEPTOR_COUNT = 22;
}
