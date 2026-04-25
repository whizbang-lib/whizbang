using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;

namespace Whizbang.Core;

/// <summary>
/// First-class admin command shipped by Whizbang for changing the resource scope of an
/// existing stream. Eliminates the need for every domain to invent its own per-domain
/// "grant access to record X" command.
/// </summary>
/// <remarks>
/// <para>
/// The receptor that handles this command (shipped by Whizbang) emits an event that
/// implements <see cref="IStreamScopeEvent"/>, so the scope change fans out to every
/// perspective registered for the stream. <see cref="Mode"/> controls whether the new
/// scope replaces the existing one wholesale or merges field-by-field.
/// </para>
/// <para>
/// Authorization gates use the standard
/// <see cref="Security.Attributes.RequirePermissionAttribute"/> on the receptor — admins
/// configure which permission keys gate the command per their own policy.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/scoping#update-stream-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/UpdateStreamScopeCommandTests.cs</tests>
[PinnedId("493b898f-08ce-4dfd-b04e-339831565bdd")]
public sealed class UpdateStreamScopeCommand : ICommand {
  /// <summary>The stream id whose perspective rows should be re-scoped.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>The proposed new scope.</summary>
  public required PerspectiveScope NewScope { get; init; }

  /// <summary>How <see cref="NewScope"/> combines with the existing row scope.</summary>
  public ScopeMutationMode Mode { get; init; } = ScopeMutationMode.Replace;
}

/// <summary>
/// How an <see cref="UpdateStreamScopeCommand"/>'s new scope combines with the existing
/// scope on each affected perspective row.
/// </summary>
public enum ScopeMutationMode {
  /// <summary>
  /// Replace the existing scope wholesale with the supplied <see cref="UpdateStreamScopeCommand.NewScope"/>.
  /// Fields not specified in NewScope become null/empty on the row.
  /// </summary>
  Replace = 0,
  /// <summary>
  /// Merge field-by-field: non-null/non-empty fields in <see cref="UpdateStreamScopeCommand.NewScope"/>
  /// overwrite the existing row's corresponding field; null/empty fields preserve the
  /// existing row's value. <see cref="PerspectiveScope.AllowedPrincipals"/> and
  /// <see cref="PerspectiveScope.Extensions"/> are concatenated and de-duplicated.
  /// </summary>
  Merge = 1,
}
