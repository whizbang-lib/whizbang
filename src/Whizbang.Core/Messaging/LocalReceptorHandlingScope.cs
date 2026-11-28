// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The handling a receptor reached by generated local dispatch runs its body in (#2633), returned by the dispatcher's
/// <c>EnterLocalReceptorHandling</c>; disposing it ends that handling and restores the caller's. At top level it holds
/// nothing and disposing it does nothing, so a top-level local invoke allocates nothing for it.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/LocalReceptorHandlingTests.cs</tests>
public readonly struct LocalReceptorHandlingScope : IDisposable {
  private readonly ReceptorHandling.Scope _scope;
  private readonly bool _entered;

  internal LocalReceptorHandlingScope(ReceptorHandling.Scope scope) {
    _scope = scope;
    _entered = true;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_entered) {
      _scope.Dispose();
    }
  }
}
