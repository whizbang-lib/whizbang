using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Messaging;

/// <summary>Registers payload-size hooks.</summary>
/// <docs>fundamentals/messages/payload-size-limit#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessagePayloadLimitsTests.cs</tests>
public static class MessagePayloadSizeServiceCollectionExtensions {
  /// <summary>
  /// Adds a hook that sees every message whose payload crosses the warning threshold or the limit. Adding
  /// the same hook type twice registers it once.
  /// </summary>
  public static IServiceCollection AddMessagePayloadSizeHook<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>(this IServiceCollection services)
      where THook : class, IMessagePayloadSizeHook {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadSizeHook, THook>());
    return services;
  }
}
