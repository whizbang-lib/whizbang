// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Branch backfill for <see cref="ScopeContextAccessor"/>'s static pointer properties: with no
/// initiating context in the current flow, the user and tenant read as absent.
/// </summary>
[Category("Security")]
public class ScopeContextAccessorBranchCoverageTests {

  [Test]
  public async Task CurrentUserIdAndTenantId_NoInitiatingContext_ReadNullAsync() {
    // The AsyncLocal write is confined to this test's own async flow.
    ScopeContextAccessor.CurrentInitiatingContext = null;

    await Assert.That(ScopeContextAccessor.CurrentUserId).IsNull()
      .Because("the user id points into the initiating context; with none there is no user");
    await Assert.That(ScopeContextAccessor.CurrentTenantId).IsNull()
      .Because("the tenant id points into the initiating context; with none there is no tenant");
  }
}
