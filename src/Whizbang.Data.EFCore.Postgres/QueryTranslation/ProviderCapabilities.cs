using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// What the loaded Entity Framework Core and Npgsql provider can do for us, and what we therefore
/// have to do ourselves.
/// </summary>
/// <remarks>
/// <para>
/// The jsonb containment rewrite exists because neither library will currently compile a perspective
/// filter into a form a GIN index can answer. That is a statement about specific versions, not a
/// permanent fact. If a future provider translates a JSON member comparison to containment on its
/// own, or exposes a public way to emit an arbitrary operator, the framework should stand its
/// rewrite down rather than keep a private path alive forever.
/// </para>
/// <para>
/// So the decision is made here, from data that is available at run time, instead of being assumed
/// at every call site. <see cref="EfCoreVersion"/> and <see cref="NpgsqlProviderVersion"/> report
/// what is actually loaded. <see cref="ValidatedEfCoreRange"/> and
/// <see cref="ValidatedNpgsqlRange"/> record what the behavior was verified against, and
/// <see cref="IsValidatedCombination"/> says whether those agree. A test asserts that they do, so a
/// package bump fails the build rather than silently changing what SQL consumers run.
/// </para>
/// <para>
/// Reading an assembly's version is AOT-safe: it inspects metadata of an assembly already loaded and
/// statically referenced, and resolves nothing by name.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ProviderCapabilitiesTests.cs</tests>
public static class ProviderCapabilities {
  /// <summary>The lowest Entity Framework Core version the containment rewrite was verified against.</summary>
  public static Version ValidatedEfCoreMinimum { get; } = new(10, 0, 0);

  /// <summary>The first Entity Framework Core version the rewrite has NOT been verified against.</summary>
  public static Version ValidatedEfCoreExclusiveMaximum { get; } = new(11, 0, 0);

  /// <summary>The lowest Npgsql provider version the containment rewrite was verified against.</summary>
  public static Version ValidatedNpgsqlMinimum { get; } = new(10, 0, 0);

  /// <summary>The first Npgsql provider version the rewrite has NOT been verified against.</summary>
  public static Version ValidatedNpgsqlExclusiveMaximum { get; } = new(11, 0, 0);

  /// <summary>A human-readable form of the verified Entity Framework Core range.</summary>
  public static string ValidatedEfCoreRange =>
    $"[{ValidatedEfCoreMinimum}, {ValidatedEfCoreExclusiveMaximum})";

  /// <summary>A human-readable form of the verified Npgsql provider range.</summary>
  public static string ValidatedNpgsqlRange =>
    $"[{ValidatedNpgsqlMinimum}, {ValidatedNpgsqlExclusiveMaximum})";

  /// <summary>The Entity Framework Core version actually loaded.</summary>
  public static Version EfCoreVersion { get; } =
    typeof(DbContext).Assembly.GetName().Version ?? new Version(0, 0, 0);

  /// <summary>The Npgsql Entity Framework Core provider version actually loaded.</summary>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "Anchoring the version read on the very type the containment rewrite depends on is the " +
      "point: if that type moves or disappears, this stops compiling, which is exactly the signal wanted.")]
  public static Version NpgsqlProviderVersion { get; } =
    typeof(PgUnknownBinaryExpression).Assembly.GetName().Version ?? new Version(0, 0, 0);

  /// <summary>
  /// Whether both loaded versions fall inside the ranges the containment rewrite was verified
  /// against.
  /// </summary>
  public static bool IsValidatedCombination =>
    EfCoreVersion >= ValidatedEfCoreMinimum
    && EfCoreVersion < ValidatedEfCoreExclusiveMaximum
    && NpgsqlProviderVersion >= ValidatedNpgsqlMinimum
    && NpgsqlProviderVersion < ValidatedNpgsqlExclusiveMaximum;

  /// <summary>
  /// Whether the provider already compiles a JSON member comparison into a containment test without
  /// help.
  /// </summary>
  /// <remarks>
  /// Currently always false. This is the pivot point: when a provider gains that translation, this
  /// becomes a version check, <see cref="ContainmentRewriteRequired"/> turns false on those versions,
  /// and the rewriter stands down with no change at any call site.
  /// </remarks>
  public static bool NativeContainmentTranslationAvailable => false;

  /// <summary>
  /// Whether the framework has to perform the containment rewrite itself.
  /// </summary>
  public static bool ContainmentRewriteRequired => !NativeContainmentTranslationAvailable;

  /// <summary>
  /// A one-line description of what is loaded and whether it is a verified combination, for logs and
  /// diagnostics.
  /// </summary>
  /// <returns>The description.</returns>
  public static string Describe() =>
    $"EF Core {EfCoreVersion} (verified {ValidatedEfCoreRange}), " +
    $"Npgsql {NpgsqlProviderVersion} (verified {ValidatedNpgsqlRange}), " +
    $"containment rewrite {(ContainmentRewriteRequired ? "required" : "not required")}, " +
    $"combination {(IsValidatedCombination ? "verified" : "NOT verified")}";
}
