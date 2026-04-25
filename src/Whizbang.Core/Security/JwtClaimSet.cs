using System.Security.Claims;

namespace Whizbang.Core.Security;

/// <summary>
/// Mutable builder for the claim set used to issue a JWT. Captures both single-valued
/// (last-write-wins) and multi-valued (one Claim instance per added value) claims so
/// downstream JWT signers — FastEndpoints' <c>JwtBearer.CreateToken</c>, IdentityModel's
/// <c>SecurityTokenDescriptor</c>, etc. — can faithfully serialize claims that have
/// multiple values (typically <c>permissions</c>, <c>groups</c>, <c>roles</c>).
/// </summary>
/// <remarks>
/// <para>
/// Most token-issuing libraries collapse a scalar dictionary entry to a single Claim,
/// which loses the multi-value semantics that
/// <see cref="Whizbang.Transports.HotChocolate.Middleware.WhizbangScopeMiddleware"/> (and
/// most OIDC consumers) expect when reading <c>permissions</c> via <c>FindAll</c>. This
/// builder keeps the two shapes distinct so emission code can call
/// <c>o.User[k] = v</c> for scalars and <c>o.User.Claims.Add(new Claim(k, v))</c> for each
/// multi-valued entry.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/jwt-claim-builder</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/JwtClaimSetTests.cs</tests>
public sealed class JwtClaimSet {
  private readonly Dictionary<string, string> _scalars = [];
  private readonly List<KeyValuePair<string, string>> _multi = [];

  /// <summary>Single-valued claims (last write wins). Read-only view.</summary>
  public IReadOnlyDictionary<string, string> Scalars => _scalars;

  /// <summary>
  /// Multi-valued claim entries. Each entry becomes a separate <see cref="Claim"/> on the
  /// emitted JWT. Read-only view.
  /// </summary>
  public IReadOnlyList<KeyValuePair<string, string>> MultiValued => _multi;

  /// <summary>
  /// Sets a scalar claim. Subsequent calls with the same name overwrite the previous value.
  /// </summary>
  public JwtClaimSet SetScalar(string name, string value) {
    _scalars[name] = value;
    return this;
  }

  /// <summary>
  /// Adds a multi-valued claim entry. Multiple calls with the same name accumulate.
  /// </summary>
  public JwtClaimSet AddMultiValued(string name, string value) {
    _multi.Add(new KeyValuePair<string, string>(name, value));
    return this;
  }

  /// <summary>
  /// Adds a multi-valued claim entry per supplied value. Empty enumerable is a no-op.
  /// </summary>
  public JwtClaimSet AddMultiValuedRange(string name, IEnumerable<string> values) {
    foreach (var v in values) {
      _multi.Add(new KeyValuePair<string, string>(name, v));
    }
    return this;
  }

  /// <summary>
  /// Materializes the full claim set as <see cref="Claim"/> instances for handing to a JWT
  /// signer that takes <c>IEnumerable&lt;Claim&gt;</c> directly. Scalars come first, then
  /// multi-valued entries in insertion order.
  /// </summary>
  public IEnumerable<Claim> ToClaims() {
    foreach (var kvp in _scalars) {
      yield return new Claim(kvp.Key, kvp.Value);
    }
    foreach (var kvp in _multi) {
      yield return new Claim(kvp.Key, kvp.Value);
    }
  }
}
