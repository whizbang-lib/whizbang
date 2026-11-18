// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

// A collective event whose runtime type has no namespace (Type.Namespace is null), for the dispatcher's
// namespace-tag fallback. The event is nested in a host declared outside any namespace: a nested type reports
// its container's namespace, so it reads as null too, and staying a private nested type keeps it out of the
// generators' message discovery exactly like the other test-local collective events.
#pragma warning disable CA1050, RCS1110 // Intentional: the case under test is a type declared without any namespace.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3903:Types should be defined in named namespaces", Justification = "A type outside any namespace is the case under test.")]
internal static class CollectiveDispatcherNamespacelessEventHost {
  /// <summary>The runtime type of the events <see cref="Create"/> returns.</summary>
  public static Type EventType => typeof(NamespacelessCollective);

  /// <summary>Creates a collective event whose runtime type has a null namespace.</summary>
  public static ICollectiveEvent Create(CollectiveScope scope) => new NamespacelessCollective(scope);

  private sealed record NamespacelessCollective(CollectiveScope Scope) : ICollectiveEvent;
}
#pragma warning restore CA1050, RCS1110
