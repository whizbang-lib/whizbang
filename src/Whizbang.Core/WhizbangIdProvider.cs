namespace Whizbang.Core;

/// <summary>
/// Global configuration for WhizbangId generation.
/// Provides a static API for setting the ID generation strategy application-wide.
/// </summary>
/// <remarks>
/// <para>
/// By default, uses <see cref="Uuid7IdProvider"/> which generates time-ordered UUIDv7 identifiers.
/// Call <see cref="SetProvider"/> during application startup to use a custom provider.
/// </para>
/// <para>
/// <strong>Example - Custom Provider:</strong>
/// <code>
/// // In your application startup
/// WhizbangIdProvider.SetProvider(new MyCustomIdProvider());
///
/// // In tests
/// WhizbangIdProvider.SetProvider(new SequentialTestIdProvider());
/// </code>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> SetProvider should only be called during application initialization
/// before any WhizbangId instances are created. Changing the provider at runtime may cause
/// inconsistent ID generation.
/// </para>
/// </remarks>
public static class WhizbangIdProvider {
  private static IWhizbangIdProvider _provider = new Uuid7IdProvider();

  /// <summary>
  /// Sets the global ID provider used by all WhizbangId types.
  /// This should be called once during application startup before any IDs are generated.
  /// </summary>
  /// <param name="provider">The provider to use for ID generation.</param>
  /// <exception cref="ArgumentNullException">Thrown when provider is null.</exception>
  /// <remarks>
  /// <para>
  /// This method should be called during application initialization, before any WhizbangId
  /// instances are created. Changing the provider at runtime may result in inconsistent
  /// ID generation across your application.
  /// </para>
  /// <para>
  /// <strong>Example:</strong>
  /// <code>
  /// // In Program.cs or Startup.cs
  /// WhizbangIdProvider.SetProvider(new MyCustomIdProvider());
  ///
  /// var builder = WebApplication.CreateBuilder(args);
  /// // ... rest of startup
  /// </code>
  /// </para>
  /// </remarks>
  public static void SetProvider(IWhizbangIdProvider provider) {
    _provider = provider ?? throw new ArgumentNullException(nameof(provider));
  }

  /// <summary>
  /// Generates a new globally unique identifier using the configured provider.
  /// This method is called by generated WhizbangId types.
  /// </summary>
  /// <returns>A new Guid value from the configured provider.</returns>
  public static Guid NewGuid() => _provider.NewGuid();
}
