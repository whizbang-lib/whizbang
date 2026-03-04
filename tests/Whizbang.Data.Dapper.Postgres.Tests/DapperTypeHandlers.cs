using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Module initializer to register test-specific Dapper type handlers.
/// </summary>
/// <remarks>
/// TrackedGuid handler is automatically registered by Whizbang.Data.Dapper.Custom.
/// This file only registers handlers specific to PostgreSQL test scenarios.
/// </remarks>
internal static class DapperTypeHandlers {
  /// <summary>
  /// Registers test-specific type handlers with Dapper at module load time.
  /// </summary>
  [ModuleInitializer]
  public static void Initialize() {
    // Register DateTimeOffset handler - handles TIMESTAMPTZ columns
    SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
  }

  /// <summary>
  /// Dapper type handler for DateTimeOffset values.
  /// Handles conversion from DateTime (which PostgreSQL sometimes returns) to DateTimeOffset.
  /// </summary>
  public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset> {
    public override DateTimeOffset Parse(object value) {
      if (value is DateTime dt) {
        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
      }
      return (DateTimeOffset)value;
    }

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) {
      parameter.Value = value;
    }
  }
}
