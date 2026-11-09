// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// The exit causes are a contract (#2126): an operator's log query matches on the <c>ExitCause</c> name, a supervisor
/// acts on the exit code, and <c>wh_instance_exits</c> stores the name. Renaming a cause, renumbering it, or moving it to
/// another code fails here.
/// </summary>
/// <docs>operations/startup/instance-exits#exit-codes</docs>
[Category("RunControl")]
public class ExitCauseContractTests {
  [Test]
  [Arguments(ExitCause.HostShutdown, "HostShutdown", 0, 0)]
  [Arguments(ExitCause.OperatorStop, "OperatorStop", 1, 0)]
  [Arguments(ExitCause.UpgradeStandDown, "UpgradeStandDown", 2, 0)]
  [Arguments(ExitCause.Crash, "Crash", 3, 1)]
  [Arguments(ExitCause.Unknown, "Unknown", 4, 2)]
  [Arguments(ExitCause.ClusterEviction, "ClusterEviction", 5, 3)]
  [Arguments(ExitCause.StartupFailure, "StartupFailure", 6, 4)]
  [Arguments(ExitCause.SchemaIncompatible, "SchemaIncompatible", 7, 5)]
  public async Task EachCause_KeepsItsName_ItsValue_AndItsExitCodeAsync(ExitCause cause, string name, int value, int exitCode) {
    await Assert.That(cause.ToString()).IsEqualTo(name);
    await Assert.That((int)cause).IsEqualTo(value);
    await Assert.That(ExitCauses.ExitCodeOf(cause)).IsEqualTo(exitCode);
    await Assert.That(ExitCauses.IsIntended(cause)).IsEqualTo(exitCode == 0)
      .Because("an intended stop exits zero; a failure exits non-zero, so the host restarts the process");
  }

  [Test]
  public async Task TheSetOfCauses_IsClosed_AtEightAsync() {
    await Assert.That(Enum.GetValues<ExitCause>().Length).IsEqualTo(8)
      .Because("a new cause is a contract change: give it an exit code here and in the docs before adding it");
  }

  [Test]
  public async Task AValueOutsideTheEnum_ExitsWithUnknownsCodeAsync() {
    await Assert.That(ExitCauses.ExitCodeOf((ExitCause)99)).IsEqualTo(2);
  }

  /// <summary>The default sink sets the process exit code; this test restores it, so the test process keeps its own.</summary>
  [Test]
  [NotInParallel("process-exit-code")]
  public async Task TheDefaultExitCode_SetsTheProcessExitCodeAsync() {
    var before = Environment.ExitCode;
    try {
      ProcessExitCode.Instance.SetExitCode(before + 41);
      await Assert.That(Environment.ExitCode).IsEqualTo(before + 41);
    } finally {
      Environment.ExitCode = before;
    }
  }
}
