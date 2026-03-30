using System.Runtime.CompilerServices;
using Whizbang.Core.Serialization;

#region NAMESPACE
namespace __NAMESPACE__;
#endregion

#region HEADER
// This region gets replaced with generated header + timestamp
#endregion

/// <summary>
/// Module initializer to register message-derived types for polymorphic serialization.
/// Registers all IEvent, ICommand, and IMessage implementations discovered in this assembly
/// with JsonContextRegistry for AOT-compatible polymorphic serialization.
/// </summary>
public static class MessageJsonContextInitializer {
  /// <summary>Registers all discovered message types for polymorphic serialization with JsonContextRegistry.</summary>
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Register JSON contexts
    JsonContextRegistry.RegisterContext(MessageJsonContext.Default);

    #region DERIVED_TYPE_REGISTRATIONS
    // This region will be replaced with RegisterDerivedType calls for each message type
    #endregion
  }
}
