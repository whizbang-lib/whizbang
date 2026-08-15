using System;
using System.Collections.Generic;

namespace Whizbang.Core.Startup;

/// <summary>
/// What an instance may do. A capability is <b>held</b> — won, not assigned — and an instance holds
/// several at once.
/// </summary>
/// <remarks>
/// Named <em>capability</em> rather than <em>role</em> deliberately: <c>Whizbang.Core.Security.Role</c>
/// already exists as a named collection of permissions for access control, and two public
/// <c>Role</c> types in one library — one meaning <em>who the caller is</em>, the other
/// <em>what this process does</em> — is a hazard in prose and logs even where the types are distinct.
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
public static class StartupCapabilities {
  /// <summary>
  /// The capability every instance holds. A step requiring it runs everywhere, which is the default
  /// and the behaviour of every step that does not ask for something narrower.
  /// </summary>
#pragma warning disable CA1707 // SCREAMING_CASE constants are the established convention here
  public const string EVERY_INSTANCE = "every-instance";
#pragma warning restore CA1707
}

/// <summary>
/// What the instances that did <em>not</em> win an exclusive capability do while its holder works.
/// </summary>
/// <docs>proposals/startup-pipeline</docs>
public enum NonHolderBehavior {
  /// <summary>
  /// Wait for the holder to finish before continuing. Correct when later steps cannot proceed
  /// without the exclusive work — nothing may reach the reconcile steps before the schema exists.
  /// </summary>
  Await,

  /// <summary>
  /// Carry on immediately. Correct when the exclusive work is genuinely independent — nothing should
  /// block on a space-reclaiming table rewrite.
  /// </summary>
  Skip,
}

/// <summary>
/// One declared step of startup: what it is, what it needs, who runs it, and whether the system may
/// be considered ready without it.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor exists so that ordering stops being an emergent property of dependency-injection
/// registration position. A step states its dependencies by <em>name</em>, and the order is derived
/// from those statements rather than from the sequence in which anything happened to register.
/// </para>
/// <para>
/// Exclusivity is not a field. It follows from <see cref="RequiredCapability"/>: a step requiring an
/// exclusive capability runs on one instance, a step requiring a shared one runs on all of them.
/// Deriving it removes the possibility of declaring a contradiction — a step cannot claim to be
/// fleet-exclusive while requiring a capability every instance holds.
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStepOrderResolverTests.cs</tests>
public sealed record StartupStepDescriptor {
  /// <summary>
  /// A stable name, unique across the pipeline. It is what other steps depend on and what appears in
  /// logs, metrics and health detail, so it is a contract rather than a label.
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// The names of steps that must complete before this one. Declared rather than implied; a name
  /// that matches no registered step is an error at resolve time, not a silently relaxed ordering.
  /// </summary>
  public IReadOnlyList<string> DependsOn { get; init; } = [];

  /// <summary>
  /// The capability an instance must hold to run this step. Defaults to
  /// <see cref="StartupCapabilities.EVERY_INSTANCE"/>, so a step that says nothing runs everywhere.
  /// </summary>
  public string RequiredCapability { get; init; } = StartupCapabilities.EVERY_INSTANCE;

  /// <summary>
  /// Where <see cref="RequiredCapability"/> is exclusive, what the instances that did not win it do.
  /// Ignored for shared capabilities, where there is no holder to wait for.
  /// </summary>
  public NonHolderBehavior NonHolderBehavior { get; init; } = NonHolderBehavior.Await;

  /// <summary>
  /// Whether the pipeline must complete this step before it is considered ready. A step whose
  /// completion is not locally decidable — anything waiting on other services — must declare
  /// <see langword="false"/>, or the pipeline deadlocks on a distributed condition.
  /// </summary>
  public bool Blocking { get; init; } = true;

  /// <summary>
  /// Whether the step runs at all. Disabling a step removes it from the order; an enabled step that
  /// depends on a disabled one is an error, because disabling must not silently weaken another
  /// step's declared ordering.
  /// </summary>
  public bool Enabled { get; init; } = true;
}

/// <summary>
/// A declared pipeline that cannot be run as written — a cycle, an unknown dependency, a duplicate
/// name, or an ordering left unsatisfiable by a disabled step.
/// </summary>
/// <remarks>
/// Thrown at resolve time, before any step executes. Failing loudly here is the point: the
/// alternative is picking one of several possible orders and starting up in it, which is the
/// behaviour the pipeline exists to replace.
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
public sealed class StartupPipelineConfigurationException : Exception {
  /// <summary>Creates the exception with a message describing what cannot be resolved.</summary>
  public StartupPipelineConfigurationException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and an underlying cause.</summary>
  public StartupPipelineConfigurationException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>Creates the exception with no message.</summary>
  public StartupPipelineConfigurationException() { }
}
