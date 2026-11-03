// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Archival;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares the end-of-life policy of the streams an event type opens (#1740, WS-3 S11): a stream's type is the type of
/// its first event, so the policy goes on that event type. Each setting is the same as its configuration key under
/// <c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;</c>, and a key set in configuration wins over the attribute's value.
/// The policies run only when <c>Whizbang:StreamLifecycle:Enabled</c> is <see langword="true"/>.
/// </summary>
/// <remarks>
/// The attribute is read at compile time: the message-type catalog generator registers each declaration with
/// <see cref="StreamEndOfLifeDeclarations"/>, without reflection. It is inherited: an event derived from an attributed event
/// declares the same policy for the streams it opens. A value that cannot be used (a time span that does not parse or is not
/// above zero, a negative count) fails the host's start naming the type.
/// </remarks>
/// <example>
/// <code>
/// [StreamEndOfLife(IdleTtl = "90.00:00:00", Action = StreamLifecycleAction.Archive)]
/// public sealed record OrderPlaced([property: StreamId] Guid OrderId) : IEvent;
/// </code>
/// </example>
/// <docs>fundamentals/events/stream-end-of-life#declaring-a-policy-on-the-event-type</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamEndOfLifeDeclarationsTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_StreamEndOfLife_RegistersTheDeclarationOfEachEventType_InheritedAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class StreamEndOfLifeAttribute : Attribute {
  /// <summary>The idle trigger as a time span (for example <c>"90.00:00:00"</c>), or <see langword="null"/> for none.</summary>
  public string? IdleTtl { get; set; }

  /// <summary>The age trigger as a time span, or <see langword="null"/> for none.</summary>
  public string? MaxAge { get; set; }

  /// <summary>The version cap: how many newest versions stay hot; 0 (the default) for none.</summary>
  public long KeepTailVersions { get; set; }

  /// <summary>The per-tenant byte cap; 0 (the default) for none.</summary>
  public long TenantByteCap { get; set; }

  /// <summary>What happens to a stream at its end of life. Default <see cref="StreamLifecycleAction.Archive"/>.</summary>
  public StreamLifecycleAction Action { get; set; } = StreamLifecycleAction.Archive;
}
