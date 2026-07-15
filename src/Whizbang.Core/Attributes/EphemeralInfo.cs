namespace Whizbang.Core.Attributes;

/// <summary>
/// The resolved ephemeral mode of a message type — the compile-time <see cref="EphemeralAttribute"/>
/// (however it was composed: directly, on a base record, or on a marker interface) reduced to its
/// effective values by the source generator. Carried on <see cref="MessageTypeCatalogEntry"/>; a null
/// <c>Ephemeral</c> there means the type is Sourced (the durable default).
/// </summary>
/// <param name="Destruction">How/when the event self-destructs.</param>
/// <param name="Storage">Where the transient state lives.</param>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralInfo(Destruction Destruction, TransientStorage Storage);
