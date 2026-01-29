using Microsoft.AspNetCore.SignalR;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.SignalR.Hooks;

namespace Whizbang.SignalR.DependencyInjection;

/// <summary>
/// Extension methods for configuring SignalR notification hooks.
/// </summary>
public static class SignalRTagExtensions {
  /// <summary>
  /// Adds SignalR notification support for <see cref="NotificationTagAttribute"/> tags.
  /// </summary>
  /// <typeparam name="THub">The SignalR hub type to use for notifications.</typeparam>
  /// <param name="options">The tag options.</param>
  /// <returns>The tag options for chaining.</returns>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   options.Tags.UseSignalR&lt;NotificationHub&gt;();
  /// });
  /// </code>
  /// </example>
  public static TagOptions UseSignalR<THub>(this TagOptions options)
      where THub : Hub {
    options.UseHook<NotificationTagAttribute, SignalRNotificationHook<THub>>();
    return options;
  }

  /// <summary>
  /// Adds SignalR notification support with a custom priority.
  /// </summary>
  /// <typeparam name="THub">The SignalR hub type to use for notifications.</typeparam>
  /// <param name="options">The tag options.</param>
  /// <param name="priority">The hook priority (lower values execute first).</param>
  /// <returns>The tag options for chaining.</returns>
  public static TagOptions UseSignalR<THub>(this TagOptions options, int priority)
      where THub : Hub {
    options.UseHook<NotificationTagAttribute, SignalRNotificationHook<THub>>(priority);
    return options;
  }
}
