// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Observability;

/// <summary>
/// The values every hop and stored envelope is stamped with, each decided in one place: the ambient
/// trace context, the writing instance's identity, and the hop list a stored copy carries.
/// </summary>
/// <remarks>
/// Each of these was written out at every call site that builds a hop or a stored envelope, so the
/// fallback each one takes (no ambient activity, a provider with no identity, an envelope that
/// arrived without hops) was decided over and over and tested at none of them. Here each fallback is
/// decided once and tested once.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs</tests>
internal static class HopStamping {
  /// <summary>
  /// The W3C <c>traceparent</c> of the ambient <see cref="Activity"/>, or null when no activity is
  /// running, so a hop records the trace it was written under.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:AmbientTraceParent_NoActivity_IsNullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:AmbientTraceParent_RunningActivity_IsItsIdAsync</tests>
  internal static string? AmbientTraceParent => Activity.Current?.Id;

  /// <summary>
  /// The identity of the instance writing a hop, or <see cref="ServiceInstanceInfo.Unknown"/> when
  /// there is no provider or the provider answers with none.
  /// </summary>
  /// <param name="provider">The instance provider, when one is registered.</param>
  /// <returns>The provider's identity, or <see cref="ServiceInstanceInfo.Unknown"/>.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:ToInfoOrUnknown_NoProvider_IsUnknownAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:ToInfoOrUnknown_ProviderWithNoInfo_IsUnknownAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:ToInfoOrUnknown_Provider_IsItsInfoAsync</tests>
  internal static ServiceInstanceInfo ToInfoOrUnknown(this IServiceInstanceProvider? provider) =>
    provider?.ToInfo() ?? ServiceInstanceInfo.Unknown;

  /// <summary>
  /// A copy of the envelope's hops for a stored or forwarded copy, so later hops added to either do
  /// not show up in the other. Empty when the envelope arrived with no hop list at all, which a
  /// deserialized envelope can.
  /// </summary>
  /// <param name="envelope">The envelope whose hops are copied.</param>
  /// <returns>A new list holding the envelope's hops, or an empty one.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:CopyHops_Hops_IsADetachedCopyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:CopyHops_NoHopList_IsEmptyAsync</tests>
  internal static List<MessageHop> CopyHops(this IMessageEnvelope envelope) =>
    envelope.Hops?.ToList() ?? [];

  /// <summary>
  /// The scope the envelope's current hops carry, for the row a received or fanned-out message is
  /// stored as; null when no current hop carries one.
  /// </summary>
  /// <param name="envelope">The envelope whose scope is read.</param>
  /// <returns>The current scope, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:CurrentScope_Scoped_IsItAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/HopStampingTests.cs:CurrentScope_Unscoped_NullAsync</tests>
  internal static PerspectiveScope? GetCurrentPerspectiveScope(this IMessageEnvelope envelope) =>
    envelope.GetCurrentScope()?.Scope;
}
