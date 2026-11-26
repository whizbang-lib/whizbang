// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The event types the application deleted and retired, bound from <c>Whizbang:EventStore:RetiredTypes</c> (#2329). A
/// retired type is an eligibility rule: no integrity digest counts it on either side of a service pair, the receive
/// boundary discards it, and the maintenance cycle purges its stored events from every stream, a bounded batch at a
/// time, whether or not the type's class still exists anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The rule is part of the type's rule hash, so two services that disagree on whether a type is retired halt their
/// pair through the pair guard until both declare it: declare a retirement in every service that stores the type.
/// </para>
/// <para>
/// A name is matched without its assembly. A name this service still declares as an event type is not retired here
/// (<see cref="EffectiveNames"/>): its events are neither purged nor turned away, since its readers here still apply
/// them, and the purge warns about it every maintenance cycle. A name nothing is stored under is reported by the
/// purge, once per process.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs</tests>
public sealed class RetiredEventTypesOptions {
  /// <summary>The configuration section: <c>Whizbang:EventStore:RetiredTypes</c>.</summary>
  public const string SECTION = "Whizbang:EventStore:RetiredTypes";

  /// <summary>The default <see cref="BatchStreams"/>.</summary>
  public const int DEFAULT_BATCH_STREAMS = 500;

  /// <summary>The default <see cref="MaxBatchesPerCycle"/>.</summary>
  public const int DEFAULT_MAX_BATCHES_PER_CYCLE = 10;

  /// <summary>
  /// The retired event types, by name, with or without the assembly (<c>Contracts.CouponIssued</c>). Empty by default:
  /// nothing is retired.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs:RetiredEventTypes_BindFromTheirSection_AndValidateAtStartupAsync</tests>
  public List<string> Names { get; } = [];

  /// <summary>
  /// The most streams one purge batch takes, each with every stored event of the retired types in it. One batch is one
  /// transaction. Default 500.
  /// </summary>
  public int BatchStreams { get; set; } = DEFAULT_BATCH_STREAMS;

  /// <summary>The most purge batches one maintenance cycle runs. Default 10.</summary>
  public int MaxBatchesPerCycle { get; set; } = DEFAULT_MAX_BATCHES_PER_CYCLE;

  /// <summary>The retired names without their assembly, each once, in ordinal order; blank names are left out.</summary>
  /// <returns>The names.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:RetiredNames_AreTheNamesWithoutTheirAssembly_OnceEach_InOrdinalOrderAsync</tests>
  public IReadOnlyList<string> RetiredNames() =>
    [.. Names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(RuleTypeName.Of).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

  /// <summary>Whether <paramref name="eventType"/>, in any form, is retired.</summary>
  /// <param name="eventType">A stored, wire or bare type name.</param>
  /// <returns>True when its name without the assembly is retired.</returns>
  public bool IsRetired(string eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    var name = RuleTypeName.Of(eventType);
    return Names.Exists(n => !string.IsNullOrWhiteSpace(n) && string.Equals(RuleTypeName.Of(n), name, StringComparison.Ordinal));
  }

  /// <summary>Whether <paramref name="eventType"/> is retired here: named, and not still declared by <paramref name="catalog"/>.</summary>
  /// <param name="eventType">A stored, wire or bare type name.</param>
  /// <param name="catalog">This service's message types, or null when it has none.</param>
  /// <returns>True when the type is retired on this service.</returns>
  public bool IsRetired(string eventType, IMessageTypeCatalog? catalog) =>
    IsRetired(eventType) && !_declared(catalog).Contains(RuleTypeName.Of(eventType));

  /// <summary>
  /// The retired names this service retires: <see cref="RetiredNames"/> without those <paramref name="catalog"/> still
  /// declares as an event type. The purge, the receive gate and the rule fingerprint all read this, so a name the
  /// service still declares is retired by none of them (owner decision, 2026-11-20).
  /// </summary>
  /// <param name="catalog">This service's message types, or null when it has none.</param>
  /// <returns>The names, without their assembly, in ordinal order.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:EffectiveNames_LeaveOutANameThisServiceStillDeclares_AndStillDeclaredNamesItAsync</tests>
  public IReadOnlyList<string> EffectiveNames(IMessageTypeCatalog? catalog) {
    var declared = _declared(catalog);
    return [.. RetiredNames().Where(n => !declared.Contains(n))];
  }

  /// <summary>
  /// The retired names <paramref name="catalog"/> still declares as an event type: configured, but not retired here, and
  /// reported every maintenance cycle until the name or the type is removed.
  /// </summary>
  /// <param name="catalog">This service's message types, or null when it has none.</param>
  /// <returns>The names, without their assembly, in ordinal order.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:EffectiveNames_LeaveOutANameThisServiceStillDeclares_AndStillDeclaredNamesItAsync</tests>
  public IReadOnlyList<string> StillDeclared(IMessageTypeCatalog? catalog) {
    var declared = _declared(catalog);
    return [.. RetiredNames().Where(declared.Contains)];
  }

  /// <summary>
  /// Why <paramref name="options"/> cannot be applied: a blank name or a batch limit below one. A name this service still
  /// declares is not a failure: it is left out of what is retired (<see cref="EffectiveNames"/>) and reported.
  /// </summary>
  /// <param name="options">The options.</param>
  /// <returns>The failures; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validate_ABlankName_OrABatchLimitBelowOne_FailsAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validate_TheDefaults_AndAnyRetiredName_PassAsync</tests>
  public static IReadOnlyList<string> Validate(RetiredEventTypesOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    if (options.Names.Exists(string.IsNullOrWhiteSpace)) {
      failures.Add($"{SECTION}:Names holds a blank name; name each retired event type.");
    }
    if (options.BatchStreams < 1) {
      failures.Add($"{SECTION}:BatchStreams must be at least 1, was {options.BatchStreams}.");
    }
    if (options.MaxBatchesPerCycle < 1) {
      failures.Add($"{SECTION}:MaxBatchesPerCycle must be at least 1, was {options.MaxBatchesPerCycle}.");
    }
    return failures;
  }

  private static HashSet<string> _declared(IMessageTypeCatalog? catalog) =>
    new((catalog?.GetAll() ?? []).Where(e => e.Kind == "event").Select(e => RuleTypeName.Of(e.ClrTypeName)), StringComparer.Ordinal);
}

/// <summary>
/// Stops the host at startup on <see cref="RetiredEventTypesOptions"/> that cannot be applied (#2329): a blank name or a
/// batch limit below one.
/// </summary>
/// <docs>fundamentals/events/retired-event-types#declaring-a-retired-type</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validator_StopsTheHostOnlyOnAValueItCannotApplyAsync</tests>
public sealed class RetiredEventTypesOptionsValidator : IValidateOptions<RetiredEventTypesOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, RetiredEventTypesOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = RetiredEventTypesOptions.Validate(options);
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
