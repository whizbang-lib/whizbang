// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Whether the application declares a message type an event, read from the message type catalog and indexed once by CLR
/// type name (current and former names). The receive path asks it for a type the consumer registry does not know (#2074):
/// an event in an absorbed namespace has no consumer here and arrives as JSON, so neither the registry nor the payload's
/// own marker can say it is an event, but the catalog the generator emits for its contracts assembly can.
/// </summary>
/// <docs>extending/internals/receptor-registry-query#what-a-service-consumes</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ReceivedInboxMessageBuilderClassificationTests.cs</tests>
internal sealed class CatalogMessageKinds {
  private const string EVENT_KIND = "event";
  private readonly Dictionary<string, bool> _isEventByClrName = new(StringComparer.Ordinal);

  /// <summary>
  /// Indexes the catalog's declared kinds. A living entry's own name wins over another entry's former name, and a former
  /// name is indexed by its bare CLR name, also when the ledger records it with the assembly the type moved from (#2264).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/FormerNameFromAnotherAssemblyTests.cs:TheDeclaredKind_IsFoundForAnEnvelopeWrittenUnderTheOldAssemblyAsync</tests>
  /// <param name="catalog">The message type catalog.</param>
  public CatalogMessageKinds(IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(catalog);
    var entries = catalog.GetAll();
    foreach (var entry in entries) {
      _isEventByClrName[entry.ClrTypeName] = string.Equals(entry.Kind, EVENT_KIND, StringComparison.Ordinal);
    }
    foreach (var entry in entries) {
      var isEvent = _isEventByClrName[entry.ClrTypeName];
      foreach (var formerName in entry.FormerClrTypeNames()) {
        _isEventByClrName.TryAdd(formerName, isEvent);
      }
    }
  }

  /// <summary>
  /// True when the catalog declares the type an event, false when it declares it something else (a command, a plain
  /// message, a perspective), and null when it does not declare it.
  /// </summary>
  /// <param name="messageTypeName">The message type name (assembly-qualified, version details allowed).</param>
  /// <returns>The declared kind's answer, or null.</returns>
  public bool? IsEvent(string messageTypeName) =>
    _isEventByClrName.TryGetValue(TypeNameFormatter.GetFullName(messageTypeName), out var isEvent) ? isEvent : null;
}
