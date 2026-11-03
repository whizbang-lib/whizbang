// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #1725: the stored-metadata options reach the SQL write paths as <c>wh_settings</c> rows, the write form and the
/// full-for list in both directions every time, the staging window only when set.
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/EventStoreMetadataSettings.cs</code-under-test>
[Category("Messaging")]
public class EventStoreMetadataSettingsTests {
  private static Dictionary<string, RetentionSetting> _byKey(IReadOnlyList<RetentionSetting> settings) =>
    settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

  [Test]
  public async Task Defaults_PublishLinkedAndAnEmptyList_ButNoStagingWindowAsync() {
    var settings = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions(), []));

    await Assert.That(settings[EventStoreMetadataSettings.WRITE_FORM_KEY].Value).IsEqualTo("Linked")
      .Because("the default form is published too, so a stale Full or Slim set before cannot outlive the configuration");
    await Assert.That(settings[EventStoreMetadataSettings.FULL_FOR_KEY].Value).IsEqualTo(string.Empty)
      .Because("an empty list is published, so a type removed from the list stops being kept full");
    await Assert.That(settings.ContainsKey(EventStoreMetadataSettings.STAGING_DAYS_KEY)).IsFalse()
      .Because("an unset window is not written, so a value set in SQL stands");
    await Assert.That(settings[EventStoreMetadataSettings.WRITE_FORM_KEY].Source).IsEqualTo("Whizbang:EventStore:Metadata:WriteForm");
    await Assert.That(settings[EventStoreMetadataSettings.FULL_FOR_KEY].Source).IsEqualTo("Whizbang:EventStore:Metadata:FullFor");
  }

  [Test]
  [Arguments(EventStoreMetadataWriteForm.Full, "Full")]
  [Arguments(EventStoreMetadataWriteForm.Slim, "Slim")]
  [Arguments(EventStoreMetadataWriteForm.Linked, "Linked")]
  public async Task EachWriteForm_IsPublishedByNameAsync(EventStoreMetadataWriteForm form, string literal) {
    var settings = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions { WriteForm = form }, []));

    await Assert.That(settings[EventStoreMetadataSettings.WRITE_FORM_KEY].Value).IsEqualTo(literal)
      .Because("the write paths compare the form by name");
    await Assert.That(settings[EventStoreMetadataSettings.WRITE_FORM_KEY].ValueType).IsEqualTo("string");
  }

  [Test]
  public async Task FullFor_JoinsConfigurationAndAttributes_SortedOnce_AndCutAtTheCommaAsync() {
    var options = new EventStoreMetadataOptions { FullFor = ["MyApp.Payments", "  MyApp.Orders.OrderPlaced, MyApp  ", "MyApp.Payments"] };

    var settings = _byKey(EventStoreMetadataSettings.SettingsFor(options, ["MyApp.Audit.Recorded", "MyApp.Payments"]));

    await Assert.That(settings[EventStoreMetadataSettings.FULL_FOR_KEY].Value)
      .IsEqualTo("MyApp.Audit.Recorded\nMyApp.Orders.OrderPlaced\nMyApp.Payments")
      .Because("configuration and attributes are one list, each name once, an assembly-qualified name meaning its type");
  }

  [Test]
  public async Task FullFor_BlankAndNullEntries_AreSkippedAsync() {
    await Assert.That(EventStoreMetadataSettings.FullForText(["", "  ", null, ", MyApp", "A.B"])).IsEqualTo("A.B");
  }

  [Test]
  public async Task FullFor_ANullConfiguredList_IsTheAttributesAloneAsync() {
    var settings = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions { FullFor = null! }, ["A.B"]));

    await Assert.That(settings[EventStoreMetadataSettings.FULL_FOR_KEY].Value).IsEqualTo("A.B");
  }

  [Test]
  public async Task StagingDays_WhenSet_IsPublishedAsAnIntegerAsync() {
    var settings = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions { StagingDays = 3 }, []));

    await Assert.That(settings[EventStoreMetadataSettings.STAGING_DAYS_KEY].Value).IsEqualTo("3");
    await Assert.That(settings[EventStoreMetadataSettings.STAGING_DAYS_KEY].ValueType).IsEqualTo("integer");
    await Assert.That(settings[EventStoreMetadataSettings.STAGING_DAYS_KEY].Source).IsEqualTo("Whizbang:EventStore:Metadata:StagingDays");
  }

  [Test]
  public async Task ConvertHistory_IsPublishedEveryTime_InBothDirectionsAsync() {
    var on = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions(), []));
    var off = _byKey(EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions { ConvertHistory = false }, []));

    await Assert.That(on[EventStoreMetadataSettings.CONVERT_HISTORY_KEY].Value).IsEqualTo("true")
      .Because("the default is published too, so a conversion switched off and back on resumes");
    await Assert.That(off[EventStoreMetadataSettings.CONVERT_HISTORY_KEY].Value).IsEqualTo("false")
      .Because("switching it off must reach the maintenance step on the next tick");
    await Assert.That(on[EventStoreMetadataSettings.CONVERT_HISTORY_KEY].ValueType).IsEqualTo("boolean");
    await Assert.That(on[EventStoreMetadataSettings.CONVERT_HISTORY_KEY].Source).IsEqualTo("Whizbang:EventStore:Metadata:ConvertHistory");
    await Assert.That(EventStoreMetadataSettings.CONVERT_HISTORY_KEY).IsEqualTo("metadata_convert_history");
  }

  [Test]
  public async Task SettingsFor_RefusesNullArgumentsAsync() {
    await Assert.That(() => EventStoreMetadataSettings.SettingsFor(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => EventStoreMetadataSettings.SettingsFor(new EventStoreMetadataOptions(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => EventStoreMetadataSettings.FullForText(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => EventStoreMetadataSettings.Validate(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Validate_ANegativeStagingWindow_IsRejectedByNameAsync() {
    var zero = EventStoreMetadataSettings.Validate(new EventStoreMetadataOptions { StagingDays = 0 });
    var negative = EventStoreMetadataSettings.Validate(new EventStoreMetadataOptions { StagingDays = -1 });

    await Assert.That(zero).IsEmpty().Because("zero keeps nothing past the current cycle, which is allowed");
    await Assert.That(negative.Single()).Contains("Whizbang:EventStore:Metadata:StagingDays").And.Contains("-1");
  }

  [Test]
  public async Task Validate_AnUndefinedWriteForm_IsRejectedByNameAsync() {
    var failures = EventStoreMetadataSettings.Validate(new EventStoreMetadataOptions { WriteForm = (EventStoreMetadataWriteForm)9 });

    await Assert.That(failures.Single()).Contains("Whizbang:EventStore:Metadata:WriteForm").And.Contains("9");
  }

  [Test]
  public async Task Validator_FailsOnAnInvalidValue_AndPassesTheDefaultsAsync() {
    var validator = new EventStoreMetadataOptionsValidator();

    await Assert.That(validator.Validate(null, new EventStoreMetadataOptions()).Succeeded).IsTrue();
    await Assert.That(validator.Validate(null, new EventStoreMetadataOptions { StagingDays = -2 }).Failed).IsTrue();
    await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
  }
}
