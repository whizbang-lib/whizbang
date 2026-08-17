namespace Whizbang.Core.Messaging;

/// <summary>
/// Marks a message as a framework CONTROL-PLANE signal — infrastructure traffic (integrity
/// checkpoints, audit manifests, re-delivery requests, repair commands) published by background
/// workers and framework receptors with no ambient user, by design.
/// </summary>
/// <remarks>
/// <para>
/// A control-plane message is not a user action: it carries no user security scope, and a strict
/// message-security policy (<see cref="Whizbang.Core.Security.MessageSecurityOptions.AllowAnonymous"/>
/// = false) must not refuse it. <see cref="Whizbang.Core.Security.DefaultMessageSecurityContextProvider"/>
/// recognizes this marker and proceeds without establishing a user context — the message is
/// trusted at the transport/cluster boundary, exactly like the framework's own storage and
/// dispatch of it. Without the exemption, a strict consumer logs a
/// <c>SecurityContextRequiredException</c> on every lifecycle stage of every checkpoint cycle AND
/// never runs the consumer-side integrity receptors at all (the same gate guards receptor
/// invocation), leaving detection silently inert.
/// </para>
/// <para>
/// The exemption is deliberately opt-in per type: domain messages keep the strict contract.
/// Consumers may mark their own infrastructure signals the same way.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ControlPlaneSecurityExemptionTests.cs</tests>
public interface IControlPlaneMessage;
