// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Processes;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Processes;

/// <summary>
/// The process seam's request, and the start information the real runner builds from it, checked without starting
/// anything: no shell, each argument passed as one argument, the environment set on the child alone, and both
/// output streams captured. A request names its program and arguments when written out, never its environment,
/// because the environment is where credentials travel.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Processes/ExternalProcessRunner.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
public sealed class ExternalProcessRequestTests {
  [Test]
  public async Task StartInfo_NoShell_ArgumentsVerbatim_EnvironmentOnTheChild_OutputCapturedAsync() {
    var request = new ExternalProcessRequest("/usr/bin/pg_repack", ["--table=\"Sales\".wh_outbox", "two words"],
      new Dictionary<string, string> { ["PGPASSWORD"] = "pw", ["PGUSER"] = "owner" });

    var info = ExternalProcessRunner.CreateStartInfo(request);

    await Assert.That(info.FileName).IsEqualTo("/usr/bin/pg_repack");
    await Assert.That(info.UseShellExecute).IsFalse().Because("no shell ever parses an argument");
    await Assert.That(info.CreateNoWindow).IsTrue();
    await Assert.That(info.Arguments).IsEmpty().Because("the single-string form would be re-split; ArgumentList is not");
    await Assert.That(info.ArgumentList).IsEquivalentTo(["--table=\"Sales\".wh_outbox", "two words"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(info.Environment["PGPASSWORD"]).IsEqualTo("pw");
    await Assert.That(info.Environment["PGUSER"]).IsEqualTo("owner");
    await Assert.That(info.RedirectStandardOutput).IsTrue();
    await Assert.That(info.RedirectStandardError).IsTrue();
    await Assert.That(info.RedirectStandardInput).IsFalse();
  }

  [Test]
  public async Task Request_ToString_NamesTheProgramAndArguments_NeverTheEnvironmentAsync() {
    var request = new ExternalProcessRequest("pg_repack", ["--version"], new Dictionary<string, string> { ["PGPASSWORD"] = "hunter2" });

    await Assert.That(request.ToString()).IsEqualTo("pg_repack --version");
  }

  [Test]
  public async Task Request_WithoutEnvironment_HasAnEmptyOneAsync() {
    var request = new ExternalProcessRequest("pg_repack", ["--version"]);

    await Assert.That(request.Environment).IsEmpty();
    await Assert.That(request.Arguments).IsEquivalentTo(["--version"]);
  }

  [Test]
  public async Task Request_NullParts_AreRefusedAsync() {
    await Assert.That(() => new ExternalProcessRequest(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => new ExternalProcessRequest("pg_repack", null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Result_NotStarted_CarriesTheReasonAsync() {
    var result = ExternalProcessResult.NotStarted("no such file");

    await Assert.That(result.Started).IsFalse();
    await Assert.That(result.ExitCode).IsEqualTo(-1);
    await Assert.That(result.StandardError).IsEqualTo("no such file");
    await Assert.That(result.StandardOutput).IsEmpty();
  }

  [Test]
  public async Task Runner_NullRequest_IsRefusedAsync() {
    await Assert.That(() => new ExternalProcessRunner().RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }
}
