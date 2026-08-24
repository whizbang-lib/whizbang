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
/// Applying this attribute is the supported way to say "I own this one's lifetime." The receptor is
/// skipped for registration and its constructor is no longer the container's problem. It also
/// silences the constructor-shape warning described below.
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
/// [SuppressReceptorRegistration]
/// public sealed class CountingReceptor(Action&lt;OrderPlaced&gt; onReceived) : IReceptor&lt;OrderPlaced&gt; {
///   public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) {
///     onReceived(message);
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </example>
/// <docs>fundamentals/receptors/receptors#manual-construction</docs>
[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SuppressReceptorRegistrationAttribute : Attribute;
