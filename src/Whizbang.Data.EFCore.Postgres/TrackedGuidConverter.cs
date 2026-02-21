using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core value converter that converts <see cref="TrackedGuid"/> to/from <see cref="Guid"/>.
/// This enables using TrackedGuid in EF Core queries and LINQ expressions.
/// </summary>
/// <remarks>
/// <para>
/// TrackedGuid has implicit conversion operators to/from Guid, but EF Core's LINQ translation
/// doesn't use these operators. This converter explicitly handles the conversion so that:
/// </para>
/// <list type="bullet">
/// <item>TrackedGuid values are stored as UUID in PostgreSQL</item>
/// <item>TrackedGuid parameters work in LINQ queries (Where, FirstOrDefault, etc.)</item>
/// <item>No manual casting is required in user code</item>
/// </list>
/// </remarks>
public class TrackedGuidConverter : ValueConverter<TrackedGuid, Guid> {
  /// <summary>
  /// Creates a new TrackedGuid to Guid value converter.
  /// </summary>
  public TrackedGuidConverter()
      : base(
          tracked => tracked.Value, // TrackedGuid to Guid (for storage)
          guid => TrackedGuid.FromExternal(guid) // Guid to TrackedGuid (from storage)
      ) { }
}

/// <summary>
/// Extension methods for configuring TrackedGuid support in EF Core.
/// </summary>
public static class TrackedGuidModelBuilderExtensions {
  /// <summary>
  /// Configures EF Core to use <see cref="TrackedGuidConverter"/> for all <see cref="TrackedGuid"/> properties.
  /// Call this in your DbContext's <see cref="DbContext.ConfigureConventions"/> method.
  /// </summary>
  /// <param name="configurationBuilder">The model configuration builder.</param>
  /// <example>
  /// <code>
  /// protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
  ///   configurationBuilder.UseTrackedGuidConversion();
  /// }
  /// </code>
  /// </example>
  public static void UseTrackedGuidConversion(this ModelConfigurationBuilder configurationBuilder) {
    configurationBuilder.Properties<TrackedGuid>()
        .HaveConversion<TrackedGuidConverter>();
  }
}
