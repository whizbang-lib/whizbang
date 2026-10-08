// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.CLI.Audit;

/// <summary>
/// The audit could not be completed: no restore output to read, an unreadable file, or an advisory
/// database that did not answer. The command reports it and exits with code 2, never with a clean
/// result, because a check that did not run proves nothing about the packages.
/// </summary>
/// <docs>tools/cli-audit#exit-codes</docs>
public sealed class AuditCheckException : Exception {
  /// <summary>Creates the exception with a generic message.</summary>
  public AuditCheckException()
    : base("The audit could not be completed.") {
  }

  /// <summary>Creates the exception.</summary>
  /// <param name="message">What could not be checked and what to do about it.</param>
  public AuditCheckException(string message)
    : base(message) {
  }

  /// <summary>Creates the exception around the failure underneath.</summary>
  /// <param name="message">What could not be checked and what to do about it.</param>
  /// <param name="innerException">The failure underneath.</param>
  public AuditCheckException(string message, Exception innerException)
    : base(message, innerException) {
  }
}
