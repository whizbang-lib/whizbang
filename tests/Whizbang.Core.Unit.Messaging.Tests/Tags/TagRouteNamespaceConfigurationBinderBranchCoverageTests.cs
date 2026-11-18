// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Branch coverage for <see cref="TagRouteNamespaceConfigurationBinder"/>: a host with no
/// configuration at all binds nothing and leaves code-declared bindings untouched.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/TagRouteNamespaceConfigurationBinder.cs</code-under-test>
[Category("Core")]
[Category("Tags")]
public class TagRouteNamespaceConfigurationBinderBranchCoverageTests {

  /// <summary>
  /// Configuration is optional for the binder. Without it, the bindings declared in code are the
  /// whole truth: nothing is added, nothing is cleared, and nothing throws.
  /// </summary>
  [Test]
  public async Task Apply_NullConfiguration_LeavesCodeDeclaredBindingsUntouchedAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");

    TagRouteNamespaceConfigurationBinder.Apply(options, configuration: null);

    await Assert.That(options.RouteNamespaceBindings.Count).IsEqualTo(1);
    await Assert.That(options.RouteNamespaceBindings["bulk-import"]).IsEqualTo("bulk");
  }
}
