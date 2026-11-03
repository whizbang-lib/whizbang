// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Core.Archival;

/// <summary>Stub (RED, #1739).</summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
public static class ArchivedStreamRowPolicies {
  /// <summary>The <c>wh_settings</c> key.</summary>
  public const string SETTING_KEY = "";

  /// <summary>Stub.</summary>
  public static void Register(string perspectiveName, ArchivedStreamRowPolicy policy) => throw new NotImplementedException("#1739");

  /// <summary>Stub.</summary>
  public static void ApplyRuntimeConfiguration(IReadOnlyDictionary<string, ArchivedStreamRowPolicy>? overrides) => throw new NotImplementedException("#1739");

  /// <summary>Stub.</summary>
  public static ArchivedStreamRowPolicy Resolve(string perspectiveName) => throw new NotImplementedException("#1739");

  /// <summary>Stub.</summary>
  public static bool RemovesRows(string perspectiveName) => throw new NotImplementedException("#1739");

  /// <summary>Stub.</summary>
  public static IReadOnlyList<KeyValuePair<string, ArchivedStreamRowPolicy>> Effective(
      IReadOnlyDictionary<string, ArchivedStreamRowPolicy>? overrides) => throw new NotImplementedException("#1739");

  /// <summary>Stub.</summary>
  public static RetentionSetting SettingFor(IReadOnlyList<KeyValuePair<string, ArchivedStreamRowPolicy>> policies) =>
    throw new NotImplementedException("#1739");
}
