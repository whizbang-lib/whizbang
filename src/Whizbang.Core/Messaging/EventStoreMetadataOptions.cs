// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// What the event store writes into an event row's metadata (<c>wh_event_body.metadata</c>), and what it does with the
/// full form.
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs</tests>
public enum EventStoreMetadataWriteForm {
  /// <summary>
  /// The row keeps the full metadata: every hop as the envelope carried it. Nothing is staged, so no routes or traces
  /// are distilled.
  /// </summary>
  Full = 0,

  /// <summary>
  /// The row keeps the slim form (message id, the first hop's timestamp, correlation, causation, stream, trace parent
  /// and the event's scope) and the route id. The full form is not kept anywhere.
  /// </summary>
  Slim = 1,

  /// <summary>
  /// The default. The row keeps the slim form and the route id, and the full form is staged in
  /// <c>wh_event_metadata_staging</c>, from which maintenance distills routes and traces.
  /// </summary>
  Linked = 2,
}

/// <summary>
/// How event rows store their metadata, bound from <c>Whizbang:EventStore:Metadata</c>
/// (<c>Whizbang__EventStore__Metadata__WriteForm</c> and so on).
/// </summary>
/// <remarks>
/// <para>
/// The write paths are SQL (both emit chains and <c>wh_append_event</c>), so these options reach them through
/// <c>wh_settings</c>: the maintenance cycle publishes them on every tick, ahead of its idle gate, as
/// <c>metadata_write_form</c>, <c>metadata_full_for</c>, <c>metadata_staging_days</c> and
/// <c>metadata_convert_history</c>. A database with no row
/// behaves as the defaults here.
/// </para>
/// </remarks>
/// <docs>data/event-store#stored-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs:EventStoreMetadata_BindsFromItsSectionAsync</tests>
public sealed class EventStoreMetadataOptions {
  /// <summary>The configuration section these options bind from.</summary>
  public const string SECTION = "Whizbang:EventStore:Metadata";

  /// <summary>
  /// What a row stores. Default <see cref="EventStoreMetadataWriteForm.Linked"/>. Publishes <c>metadata_write_form</c>.
  /// </summary>
  public EventStoreMetadataWriteForm WriteForm { get; set; } = EventStoreMetadataWriteForm.Linked;

  /// <summary>
  /// Event types whose rows keep their full metadata whatever <see cref="WriteForm"/> says: a type name
  /// (<c>MyApp.Orders.OrderPlaced</c>) or a namespace (<c>MyApp.Payments</c>, which covers every type in it and below
  /// it). Joined with the types marked <c>[StoredMetadata(StoredMetadataForm.Full)]</c> and published as
  /// <c>metadata_full_for</c>.
  /// </summary>
  public List<string> FullFor { get; set; } = [];

  /// <summary>
  /// Days a staged full form is kept when maintenance has not distilled it. Publishes <c>metadata_staging_days</c>
  /// when set; the database default is 2. Unset leaves a value set in SQL alone.
  /// </summary>
  public int? StagingDays { get; set; }

  /// <summary>
  /// Whether maintenance converts rows written with full metadata before the slim form existed (rows without a route id)
  /// to the slim form plus their route id, once, in bounded batches. Default <see langword="true"/>. It converts nothing
  /// under <see cref="EventStoreMetadataWriteForm.Full"/> or debug mode, and never a row of a type kept full. Publishes
  /// <c>metadata_convert_history</c>.
  /// </summary>
  /// <docs>data/event-store#history-conversion</docs>
  public bool ConvertHistory { get; set; } = true;
}
