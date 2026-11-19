// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// <see cref="PostgresStreamFinalizer.ToStatus"/>: the statuses <c>wh_finalize_stream</c> returns (migration 266), and a
/// loud failure for one this version does not know, rather than a guess.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
public class PostgresStreamFinalizerStatusTests {
  [Test]
  [Arguments("sealed", StreamFinalizeStatus.Sealed)]
  [Arguments("already_sealed", StreamFinalizeStatus.AlreadySealed)]
  [Arguments("nothing_to_seal", StreamFinalizeStatus.NothingToSeal)]
  public async Task ToStatus_EachStatusTheFunctionReturns_IsReadAsync(string status, StreamFinalizeStatus expected) =>
    await Assert.That(PostgresStreamFinalizer.ToStatus(status)).IsEqualTo(expected);

  [Test]
  public async Task ToStatus_AStatusThisVersionDoesNotKnow_ThrowsAsync() =>
    await Assert.That(() => PostgresStreamFinalizer.ToStatus("reopened")).Throws<InvalidOperationException>()
      .Because("a newer store's status is not one of these, and reading it as any of them would misreport the seal");
}
