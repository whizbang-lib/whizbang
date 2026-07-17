using Whizbang.Core.Attributes;

namespace Whizbang.Core;

/// <summary>
/// The framework-shipped <em>default ephemeral profile</em>. Implementing this marks an event ephemeral
/// with the defaults (<see cref="Destruction.WhenConsumed"/>, <see cref="TransientStorage.PersistedRow"/>) —
/// equivalent to a bare <c>[Ephemeral]</c>, for the no-config case.
/// </summary>
/// <remarks>
/// This is <strong>not</strong> a separate mechanism. It is simply an <see cref="IEvent"/>-derived
/// interface carrying <see cref="EphemeralAttribute"/>, resolved through the exact same base/interface
/// walk as any developer-authored profile interface. Teams define their own profiles the same way (put
/// <c>[Ephemeral(...)]</c> on their own <see cref="IEvent"/>-derived interface or base record).
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Ephemeral]
public interface IEphemeralEvent : IEvent;
