// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Carries <see cref="EventStoreMetadataOptions"/> and the <c>[StoredMetadata]</c> registrations to the
/// <c>wh_settings</c> rows the write paths read (#1725).
/// </summary>
/// <remarks>
/// <para>
/// <c>metadata_write_form</c> and <c>metadata_full_for</c> are published every time, in both directions: the write
/// paths decide every event from them, so a stale value (a type no longer listed, a form switched back) would go on
/// shaping rows after the configuration changed. <c>metadata_staging_days</c> follows the retention rule instead: unset
/// is not written, so a window set in SQL stands.
/// </para>
/// <para>
/// An entry of the full list is a type name or a namespace. It is trimmed, and anything from its first comma on is
/// dropped, so an assembly-qualified name (<c>MyApp.Orders.OrderPlaced, MyApp</c>) means its type. Empty entries are
/// skipped, and the list is published sorted, each name once, one per line. Lines rather than JSON: the write paths
/// split it with <c>string_to_array</c>, which cannot fail, so a value an operator mistypes by hand can never fail an
/// event's write.
/// </para>
/// </remarks>
/// <docs>data/event-store#stored-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs</tests>
public static class EventStoreMetadataSettings {
  /// <summary>The <c>wh_settings</c> key holding the write form (<c>Full</c>, <c>Slim</c> or <c>Linked</c>).</summary>
  public const string WRITE_FORM_KEY = "metadata_write_form";

  /// <summary>The <c>wh_settings</c> key holding the full-for list: type names and namespaces, one per line.</summary>
  public const string FULL_FOR_KEY = "metadata_full_for";

  /// <summary>The <c>wh_settings</c> key holding the staging window in days.</summary>
  public const string STAGING_DAYS_KEY = "metadata_staging_days";

  private const string SECTION = EventStoreMetadataOptions.SECTION + ":";

  /// <summary>The settings to publish for <paramref name="options"/> and the attribute-declared full types.</summary>
  /// <param name="options">The bound options.</param>
  /// <param name="attributeFullTypes">The type names <c>[StoredMetadata(StoredMetadataForm.Full)]</c> declares
  /// (<see cref="StoredMetadataRegistry.FullTypeNames"/>).</param>
  /// <returns>One entry per key to write, each key once.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:Defaults_PublishLinkedAndAnEmptyList_ButNoStagingWindowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:FullFor_JoinsConfigurationAndAttributes_SortedOnce_AndCutAtTheCommaAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:StagingDays_WhenSet_IsPublishedAsAnIntegerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:EachWriteForm_IsPublishedByNameAsync</tests>
  public static IReadOnlyList<RetentionSetting> SettingsFor(EventStoreMetadataOptions options, IEnumerable<string> attributeFullTypes) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(attributeFullTypes);

    List<RetentionSetting> settings = [
      new(WRITE_FORM_KEY, options.WriteForm.ToString(), "string", SECTION + nameof(EventStoreMetadataOptions.WriteForm)),
      new(FULL_FOR_KEY, FullForText([.. options.FullFor ?? [], .. attributeFullTypes]), "string",
        SECTION + nameof(EventStoreMetadataOptions.FullFor)),
    ];
    if (options.StagingDays is int days) {
      settings.Add(new RetentionSetting(STAGING_DAYS_KEY, days.ToString(CultureInfo.InvariantCulture), "integer",
        SECTION + nameof(EventStoreMetadataOptions.StagingDays)));
    }
    return settings;
  }

  /// <summary>Renders full-for entries as the text the write paths read: trimmed, cut at a comma, sorted, once, one per line.</summary>
  /// <param name="entries">Type names and namespaces.</param>
  /// <returns>The names joined with a line feed; empty for none.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:FullFor_JoinsConfigurationAndAttributes_SortedOnce_AndCutAtTheCommaAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:FullFor_BlankAndNullEntries_AreSkippedAsync</tests>
  public static string FullForText(IEnumerable<string?> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    return string.Join('\n', entries
      .Select(static entry => _typeNamePart(entry))
      .Where(static name => name.Length > 0)
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal));
  }

  /// <summary>Configuration errors in the options: a negative staging window, or a write form that is not defined.</summary>
  /// <param name="options">The bound options.</param>
  /// <returns>One message per invalid value; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:Validate_ANegativeStagingWindow_IsRejectedByNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:Validate_AnUndefinedWriteForm_IsRejectedByNameAsync</tests>
  public static IReadOnlyList<string> Validate(EventStoreMetadataOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    if (!Enum.IsDefined(options.WriteForm)) {
      failures.Add($"{SECTION}{nameof(EventStoreMetadataOptions.WriteForm)} must be Full, Slim or Linked (was {(int)options.WriteForm}).");
    }
    if (options.StagingDays is int days && days < 0) {
      failures.Add($"{SECTION}{nameof(EventStoreMetadataOptions.StagingDays)} must be zero or greater (was "
        + $"{days.ToString(CultureInfo.InvariantCulture)}); it sets wh_settings.{STAGING_DAYS_KEY}.");
    }
    return failures;
  }

  // "MyApp.Orders.OrderPlaced, MyApp" names the type MyApp.Orders.OrderPlaced; the write paths compare type names.
  private static string _typeNamePart(string? entry) =>
    entry is null ? string.Empty : TypeNameFormatter.GetFullName(entry.Trim());
}
