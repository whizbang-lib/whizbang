// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Routing;

/// <summary>
/// Turns the opt-out list (<see cref="RoutingOptions.ExcludedEventTypes"/>, #2175) into the subjects each event
/// subscription excludes, and runs the startup checks on it.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#excluded-event-types</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/ExcludedEventTypePlannerTests.cs</tests>
internal static partial class ExcludedEventTypePlanner {
  /// <summary>
  /// The excluded subjects per subscribed event topic. Every topic in <paramref name="eventTopics"/> is in the answer,
  /// with an empty list when nothing published to it is excluded.
  /// </summary>
  /// <param name="options">The routing options holding the opt-out list.</param>
  /// <param name="eventTopics">The event topics this service subscribes to.</param>
  /// <param name="consumers">What this service consumes.</param>
  /// <param name="runtimeEventTypes">Events consumed through receptors registered at startup.</param>
  /// <param name="messageCatalog">The application's message types, or null when none is known.</param>
  /// <param name="logger">Receives the warnings and the effective exclusions.</param>
  /// <returns>Topic to ordered, distinct excluded subjects.</returns>
  /// <exception cref="InvalidOperationException">Thrown when an excluded type is one this service consumes.</exception>
  /// <remarks>
  /// An excluded type on a namespace this service absorbs is allowed, with a warning: excluding one type of an absorbed
  /// namespace is a deliberate refinement, and that type is then neither delivered nor stored.
  /// </remarks>
  internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Plan(
      RoutingOptions options,
      IReadOnlySet<string> eventTopics,
      IConsumerRegistry consumers,
      IEnumerable<Type> runtimeEventTypes,
      IMessageTypeCatalog? messageCatalog,
      ILogger logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(eventTopics);
    ArgumentNullException.ThrowIfNull(consumers);
    ArgumentNullException.ThrowIfNull(runtimeEventTypes);
    ArgumentNullException.ThrowIfNull(logger);

    var plan = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var topic in eventTopics) {
      plan[topic] = new SortedSet<string>(StringComparer.Ordinal);
    }

    if (options.ExcludedEventTypes.Count > 0) {
      _planExclusions(options, plan, consumers, runtimeEventTypes, messageCatalog, logger);
    }

    return plan.ToDictionary(
      entry => entry.Key,
      entry => (IReadOnlyList<string>)[.. entry.Value],
      StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>Checks every excluded type and adds each one to the topic it is published to.</summary>
  private static void _planExclusions(
      RoutingOptions options,
      Dictionary<string, SortedSet<string>> plan,
      IConsumerRegistry consumers,
      IEnumerable<Type> runtimeEventTypes,
      IMessageTypeCatalog? messageCatalog,
      ILogger logger) {
    // The subjects this service consumes. A subject is what the broker filters on, so an excluded subject that matches
    // a consumed one would drop a message this service handles: the comparison is the broker's own.
    var consumedSubjects = new HashSet<string>(
      consumers.GetConsumedMessages().Select(consumed => EventSubject.For(consumed.TypeName)), StringComparer.Ordinal);
    consumedSubjects.UnionWith(runtimeEventTypes.Select(EventSubject.For));

    HashSet<string>? declaredSubjects = messageCatalog is { IsAvailable: true }
      ? new HashSet<string>(messageCatalog.GetAll().Select(entry => EventSubject.For(entry.ClrTypeName)), StringComparer.Ordinal)
      : null;

    var violations = new List<string>();
    foreach (var typeName in options.ExcludedEventTypes.Order(StringComparer.Ordinal)) {
      var subject = EventSubject.For(typeName);
      var topic = TypeNameFormatter.GetNamespace(typeName)?.ToLowerInvariant();

      if (consumedSubjects.Contains(subject) || consumers.HasAnyConsumer(typeName)) {
        violations.Add($"'{typeName}' (this service consumes it)");
        continue;
      }
      if (topic is not null && options.AbsorbedNamespaces.Contains(topic)) {
        // Allowed (owner decision on #2175): excluding one type of an absorbed namespace is a deliberate refinement of
        // the absorb, but that type is then never stored, so say so.
        LogAbsorbedNamespaceExclusion(logger, topic, typeName);
      }

      if (declaredSubjects?.Contains(subject) == false) {
        LogUndeclaredType(logger, typeName);
      }

      if (topic is null || !plan.TryGetValue(topic, out var subjects)) {
        LogUnsubscribedTopic(logger, typeName, topic ?? "");
        continue;
      }
      subjects.Add(subject);
    }

    if (violations.Count > 0) {
      throw new InvalidOperationException(
        "Routing excludes event types this service cannot do without: " + string.Join(", ", violations)
        + ". An excluded type is dropped by the broker and never reaches this service, so a type it consumes (through "
        + "a receptor, a perspective, a tag consumer or a runtime subscription) would silently stop arriving. Remove "
        + "it from ExcludeEventType / Whizbang:Routing:ExcludedEventTypes, or stop consuming it.");
    }

    foreach (var (topic, subjects) in plan.Where(entry => entry.Value.Count > 0)) {
      LogEffectiveExclusions(logger, topic, subjects.Count, subjects);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Excluded event type '{EventType}' is not a message type this application declares. It is still excluded, in case a peer publishes it, but check the name for a typo")]
  private static partial void LogUndeclaredType(ILogger logger, string eventType);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Excluded event type '{EventType}' is published to topic '{Topic}', which this service does not subscribe to, so it is never delivered here and excluding it has no effect")]
  private static partial void LogUnsubscribedTopic(ILogger logger, string eventType, string topic);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Event subscription to topic '{Topic}' excludes {Count} event type(s) at the broker: {Subjects}")]
  private static partial void LogEffectiveExclusions(ILogger logger, string topic, int count, IEnumerable<string> subjects);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "absorbed namespace {Namespace}: event type {EventType} is excluded at the broker and will not be delivered")]
  private static partial void LogAbsorbedNamespaceExclusion(ILogger logger, string @namespace, string eventType);
}
