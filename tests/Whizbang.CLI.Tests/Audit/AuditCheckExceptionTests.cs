// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditCheckException"/>'s standard constructors.
/// </summary>
/// <tests>Whizbang.CLI/Audit/AuditCheckException.cs</tests>
public class AuditCheckExceptionTests {

  [Test]
  public async Task DefaultConstructor_SaysTheAuditDidNotCompleteAsync() {
    // The message is printed as-is; an empty one would leave a failed check unexplained.
    await Assert.That(new AuditCheckException().Message).IsEqualTo("The audit could not be completed.");
  }

  [Test]
  public async Task MessageConstructor_KeepsTheMessageAsync() {
    await Assert.That(new AuditCheckException("run dotnet restore first").Message).IsEqualTo("run dotnet restore first");
  }

  [Test]
  public async Task InnerConstructor_KeepsTheCauseAsync() {
    var cause = new IOException("locked");

    var exception = new AuditCheckException("could not read", cause);

    await Assert.That(exception.Message).IsEqualTo("could not read");
    await Assert.That(exception.InnerException).IsSameReferenceAs(cause);
  }
}
