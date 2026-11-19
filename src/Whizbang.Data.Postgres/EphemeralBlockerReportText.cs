// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Whizbang.Core;

namespace Whizbang.Data.Postgres;

/// <summary>One row of the stored blocker report (<c>wh_ephemeral_blockers_report</c>).</summary>
/// <param name="EphemeralType">The ephemeral type whose history is held back, as stored.</param>
/// <param name="BlockingType">The durable type in its streams, as stored.</param>
/// <param name="Events">The ephemeral type's events it keeps Sourced in the current walk.</param>
/// <param name="Streams">Their streams, each once.</param>
/// <param name="Bytes">Their approximate stored size.</param>
/// <param name="SampleStream">One of the streams.</param>
/// <param name="WalkComplete">Whether the ephemeral type's walk is complete.</param>
/// <param name="Service">The service of the hop that wrote the blocking type, from its newest route.</param>
/// <param name="Topic">The topic of that hop.</param>
/// <param name="CallerClass">The caller class of that hop.</param>
/// <param name="CallerMember">The caller member of that hop.</param>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
internal sealed record EphemeralBlocker(
  string EphemeralType, string BlockingType, long Events, long Streams, long Bytes, Guid SampleStream, bool WalkComplete,
  string? Service, string? Topic, string? CallerClass, string? CallerMember);

/// <summary>One durable input of a read model opted into mixing (<c>wh_ephemeral_mixed_inputs</c>).</summary>
/// <param name="EventType">The durable input type, as stored.</param>
/// <param name="Events">Its Sourced events still stored.</param>
/// <param name="Streams">Their streams.</param>
/// <param name="MixedStreams">Those streams that also hold one of the read model's ephemeral inputs.</param>
/// <docs>fundamentals/events/ephemeral-events#read-models-opted-into-mixing</docs>
internal sealed record EphemeralMixedInput(string EventType, long Events, long Streams, long MixedStreams);

/// <summary>
/// The text of the blocker report: per blocking type its counts, a sample stream, its route and the four things an
/// operator can do about it: mark it ephemeral, release it (with the exact setting line and the read models that would lose
/// their rebuild from the released events), keep it, or allow mixing (with the exact attribute line and the read models that
/// must carry it, #2255).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_AMixedStream_NamesTheBlockingType_ItsCounts_ASampleStream_AndItsRouteAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_ARouteWithOnlySomeParts_NamesWhatItHasAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_WithoutARoute_OrAReadModel_SaysSoAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_NamesTheReadModelsThatApplyTheBlockingType_NextToTheReleaseLineAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Report_AnAllowedPair_IsNoBlocker_AndAnUnallowedOneOffersAllowMixingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Report_WhenNoReadModelAppliesBoth_SaysAllowMixingNeedsOneAsync</tests>
internal static class EphemeralBlockerReportText {
  /// <summary>The setting section a release is written under.</summary>
  internal const string RELEASE_SECTION = "Whizbang:Ephemeral:ReleaseBlockers";

  /// <summary>The attribute line that allows a pair to share a stream (#2255).</summary>
  internal const string ALLOW_MIXING_ATTRIBUTE = "[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]";

  /// <summary>Appends one blocker's entry.</summary>
  /// <param name="text">The report being built.</param>
  /// <param name="blocker">The blocker.</param>
  /// <param name="releaseIndex">The index the setting line takes in the ephemeral type's release list.</param>
  /// <param name="readModels">The read models that apply the blocking type.</param>
  /// <param name="mixingReadModels">The read models that apply both the ephemeral and the blocking type: each must carry
  /// the allow-mixing attribute for the pair to share a stream.</param>
  public static void AppendEntry(
      StringBuilder text, EphemeralBlocker blocker, int releaseIndex, IReadOnlyList<string> readModels, IReadOnlyList<string> mixingReadModels) {
    var culture = CultureInfo.InvariantCulture;
    text.Append(culture, $"\n  {blocker.BlockingType}: {blocker.Events} event(s) in {blocker.Streams} stream(s), ")
      .Append(culture, $"about {blocker.Bytes:N0} bytes; sample stream {blocker.SampleStream}")
      .Append("\n    ").Append(_route(blocker))
      .Append(culture, $"\n    to clear it: (1) mark {TypeNameFormatter.GetFullName(blocker.BlockingType)} [Ephemeral] if it is transient everywhere; ")
      .Append(culture, $"(2) release it in these streams only with the setting {RELEASE_SECTION}:")
      .Append(culture, $"{TypeNameFormatter.GetFullName(blocker.EphemeralType)}:{releaseIndex} = {blocker.BlockingType} ")
      .Append(readModels.Count == 0
        ? "(no registered read model applies it)"
        : "(read models that apply it lose their rebuild from the reaped events: " + string.Join(", ", readModels) + ")")
      .Append("; (3) keep it: these events and streams stay stored; ")
      .Append("(4) allow mixing, which keeps these durable events and lets the ephemeral ones go: put ")
      .Append(ALLOW_MIXING_ATTRIBUTE)
      .Append(mixingReadModels.Count == 0
        ? " on a read model that applies both types (no registered read model applies both, so there is none to opt in yet)."
        : " on every read model that applies both types: " + string.Join(", ", mixingReadModels) + ".");
  }

  /// <summary>
  /// The report of one read model opted into mixing: ready to drop the opt-in when none of its durable inputs has a
  /// stored event left, otherwise each input's counts, linked to the blocker entries it appears in.
  /// </summary>
  /// <param name="inputs">The read model's durable inputs and their stored counts.</param>
  /// <param name="blockers">The stored blocker report.</param>
  /// <returns>The report text.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_WithNoDurableInputLeft_IsReportedReadyToDropTheAttributeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_WithDurableInputsLeft_ReportsTheirCounts_InTheirOwnStreamsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_ADurableInputSharingStreams_IsLinkedToItsBlockerEntryAsync</tests>
  public static string MixedReadModel(IReadOnlyList<EphemeralMixedInput> inputs, IReadOnlyList<EphemeralBlocker> blockers) {
    if (inputs.All(i => i.Events == 0)) {
      return "ready to drop the attribute: no durable event of its durable inputs (" + string.Join(", ", inputs.Select(i => i.EventType))
        + ") is stored; remove [DangerouslyAllowMixedEphemeralAndSourcedEvents] and its durable Apply methods to make it purely "
        + "ephemeral (its rebuild is then refused, as for any ephemeral read model).";
    }
    var culture = CultureInfo.InvariantCulture;
    var text = new StringBuilder("durable events remain:");
    foreach (var input in inputs) {
      text.Append(culture, $"\n  {input.EventType}: {input.Events} stored event(s) in {input.Streams} stream(s), {input.MixedStreams} holding both kinds");
      var held = blockers.Where(b => string.Equals(b.BlockingType, input.EventType, StringComparison.Ordinal))
        .Select(b => b.EphemeralType).Distinct(StringComparer.Ordinal).ToList();
      if (held.Count > 0) {
        text.Append(culture, $"; it holds back the history of {string.Join(", ", held)} (see the blocker report); if those streams are ")
          .Append(culture, $"disposable, release it there with the {RELEASE_SECTION} line the blocker report prints");
      }
    }
    return text.ToString();
  }

  // Where the blocking type comes from: the hop that wrote it on its newest route, as much of it as was recorded.
  private static string _route(EphemeralBlocker blocker) {
    var parts = new List<string>(3);
    if (blocker.Service is not null) {
      parts.Add("service " + blocker.Service);
    }
    if (blocker.Topic is not null) {
      parts.Add("topic " + blocker.Topic);
    }
    var caller = string.Join('.', new[] { blocker.CallerClass, blocker.CallerMember }.OfType<string>());
    if (caller.Length > 0) {
      parts.Add(caller);
    }
    return parts.Count == 0 ? "no route recorded for it" : "written by " + string.Join(", ", parts);
  }
}
