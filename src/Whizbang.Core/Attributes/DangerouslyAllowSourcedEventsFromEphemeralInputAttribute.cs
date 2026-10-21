// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Explicit, in-code opt-in that silences <c>WHIZ132</c> on a receptor that deliberately returns or cascades a Sourced
/// event from an ephemeral message. Applying it is a decision: you accept that the Sourced event is a durable fact that
/// is never reaped, and you own making sure it does not carry what the ephemeral message was declared ephemeral to
/// avoid keeping.
/// </summary>
/// <remarks>
/// <para>
/// The name is intentionally long and alarming, like <see cref="DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute"/>:
/// nobody types it by accident, it shows up in code review as a named choice, and it is trivially greppable.
/// </para>
/// <para>
/// It exists because a <c>#pragma warning disable WHIZ132</c> or an <c>.editorconfig</c> override is unavailable to
/// teams that run warnings-as-errors with a no-suppression policy. Such a team would otherwise be hard-blocked from a
/// receptor that legitimately records a business fact in response to an ephemeral signal. The analyzer matches it by
/// simple name, so it holds even when the attribute does not bind.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/EphemeralLaunderingAnalyzerTests.cs:DeliberateReceptor_WithTheOptIn_NoWHIZ132Async</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class DangerouslyAllowSourcedEventsFromEphemeralInputAttribute : Attribute;
