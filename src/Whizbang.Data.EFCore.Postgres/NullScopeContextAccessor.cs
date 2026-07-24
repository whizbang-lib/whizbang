using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// No-op scope context accessor for backward compatibility.
/// Used when constructing lens queries without scope support (e.g., in tests).
/// </summary>
internal sealed class NullScopeContextAccessor : IScopeContextAccessor {
  internal static readonly NullScopeContextAccessor Instance = new();

  public IScopeContext? Current { get; set; }
  public IMessageContext? InitiatingContext { get; set; }
}

/// <summary>
/// Default options wrapper for backward compatibility.
/// Returns WhizbangCoreOptions with Global default scope (no filtering).
/// </summary>
internal sealed class GlobalScopeOptions : IOptions<WhizbangCoreOptions> {
  internal static readonly GlobalScopeOptions Instance = new();

  public WhizbangCoreOptions Value { get; } = new() { DefaultQueryScope = QueryScope.Global };
}
