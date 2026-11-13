// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Declares that a receptor is constructed by hand and must NOT be registered in dependency
/// injection by receptor discovery.
/// </summary>
/// <remarks>
/// <para>
/// Discovery registers every <c>IReceptor&lt;T&gt;</c> implementation it finds. That is the right
/// default — a receptor exists to be resolved and dispatched to. But some receptors are deliberately
/// built by their owner rather than resolved: a test helper closing over per-test state, a receptor
/// parameterized by a callback, or one whose collaborator is chosen at runtime. Their constructors
/// take arguments the container has no way to supply, and nothing sensibly could supply them.
/// </para>
/// <para>
/// Without this attribute such a receptor is a latent outage. The container validates every
/// registered descriptor when <c>BuildServiceProvider</c> runs with validation enabled, so ONE
/// un-constructible receptor aborts construction of the ENTIRE provider — taking down every service
/// in the assembly, not just that receptor. The resulting error names the receptor but never
/// explains why it was registered at all, which reads as an inexplicable DI misconfiguration.
/// </para>
/// <para>
/// Applying this attribute is the supported way to say "I own this one's lifetime." It does not mean "ignore this
/// type": the receptor is still discovered and still routed at its stages. It is skipped for registration, its
/// constructor is no longer the container's problem, and the constructor-shape warning (WHIZ014) is silenced.
/// </para>
/// <para>
/// The owner registers it in one of two ways: by hand in dependency injection (the instance as its own class, or keyed
/// by its class name), or at runtime through <see cref="Messaging.IReceptorRegistry.Register{TMessage}"/> at the stage it
/// should run at. A route compiled for it runs the instance registered by hand, and skips the receptor when there is
/// none, logging the skip once per receptor type at Debug (#2309): the route cannot tell a receptor registered only at
/// runtime from one that was forgotten, and a missing registration of a receptor the owner runs at runtime is not an
/// error. See <see cref="Messaging.SuppressedReceptorRoute"/>. A receptor without this attribute is registered by
/// discovery, so its route still throws when the registration is missing.
/// </para>
/// <para>
/// Note what this deliberately does NOT do: it does not teach the framework to skip receptors it
/// merely fails to construct. Silently dropping those would turn a forgotten dependency registration
/// into a receptor that never fires — a failure that is invisible until messages quietly stop being
/// handled. Opting out has to be a decision someone wrote down, not something inferred.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Built by hand so it can close over state the container knows nothing about.
/// [FireAt(LifecycleStage.PostDistributeInline)]
/// [SuppressReceptorRegistration]
/// public sealed class CountingReceptor(Action&lt;OrderPlaced&gt; onReceived) : IReceptor&lt;OrderPlaced&gt; {
///   public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) {
///     onReceived(message);
///     return ValueTask.CompletedTask;
///   }
/// }
///
/// // Registered by hand ...
/// services.AddSingleton(new CountingReceptor(received.Add));
/// // ... or at runtime, at the stage it runs at.
/// registry.Register&lt;OrderPlaced&gt;(new CountingReceptor(received.Add), LifecycleStage.PostDistributeInline);
/// </code>
/// </example>
/// <docs>fundamentals/receptors/receptors#manual-construction</docs>
/// <docs>fundamentals/receptors/receptors#registering-a-hand-built-receptor</docs>
/// <docs>fundamentals/receptors/receptors#unregistered-suppressed-receptor</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/SuppressedReceptorRouteTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorRegistrationSuppressionTests.cs</tests>
[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SuppressReceptorRegistrationAttribute : Attribute;
