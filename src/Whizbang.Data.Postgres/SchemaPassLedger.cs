// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Whizbang.Data.Postgres;

/// <summary>What a schema pass records so a pass applied one step at a time can be resumed.</summary>
public static class SchemaPassLedger {
  /// <summary>The owner of step rows.</summary>
  public const string STEP_OWNER = "step";
  /// <summary>The hash of a file that has to run again.</summary>
  public const string PENDING_HASH = "pending-rerun";
  /// <summary>Pending.</summary>
  public const short PENDING = 0;
  /// <summary>Applied.</summary>
  public const short APPLIED = 1;
  /// <summary>Failed.</summary>
  public const short FAILED = -1;

  /// <summary>The ledger key of a step.</summary>
  public static string StepKey(string context, string step) => throw new NotImplementedException();
  /// <summary>Whether a status is owed.</summary>
  public static bool IsOutstanding(short status) => throw new NotImplementedException();
  /// <summary>A hash over a build.</summary>
  public static string Fingerprint(IEnumerable<string> parts) => throw new NotImplementedException();
  /// <summary>What a pass marks and runs.</summary>
  public static SchemaPassSteps Plan(
      IReadOnlyDictionary<string, (string Fingerprint, short Status)> rows, IEnumerable<string> needed, string fingerprint) =>
    throw new NotImplementedException();
}

/// <summary>What a plan decided.</summary>
public sealed record SchemaPassSteps(IReadOnlyList<string> ToMark, IReadOnlySet<string> ToRun);
