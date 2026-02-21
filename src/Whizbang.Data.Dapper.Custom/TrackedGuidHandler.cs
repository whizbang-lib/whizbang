using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper type handler for TrackedGuid value objects.
/// Converts TrackedGuid to Guid for database storage and Guid to TrackedGuid on read.
/// </summary>
/// <remarks>
/// This handler is automatically registered via the <see cref="DapperTypeHandlerInitializer"/>
/// module initializer when the assembly is loaded.
/// </remarks>
public sealed class TrackedGuidHandler : SqlMapper.TypeHandler<TrackedGuid> {
  /// <summary>
  /// Parses a database value to TrackedGuid.
  /// </summary>
  /// <param name="value">The database value (expected to be Guid).</param>
  /// <returns>A TrackedGuid created from the database value.</returns>
  public override TrackedGuid Parse(object value) {
    if (value is Guid guid) {
      return TrackedGuid.FromExternal(guid);
    }
    throw new InvalidCastException($"Cannot convert {value?.GetType()} to TrackedGuid");
  }

  /// <summary>
  /// Sets a TrackedGuid value on a database parameter.
  /// </summary>
  /// <param name="parameter">The database parameter to set.</param>
  /// <param name="value">The TrackedGuid value to store.</param>
  public override void SetValue(IDbDataParameter parameter, TrackedGuid value) {
    // TrackedGuid has implicit conversion to Guid
    parameter.Value = (Guid)value;
  }
}

/// <summary>
/// Module initializer to register Dapper type handlers when the assembly is loaded.
/// </summary>
internal static class DapperTypeHandlerInitializer {
  /// <summary>
  /// Registers Whizbang type handlers with Dapper at module load time.
  /// </summary>
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible Dapper handler registration
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Register TrackedGuid handler - converts to/from Guid for database UUID columns
    SqlMapper.AddTypeHandler(new TrackedGuidHandler());
  }
}
