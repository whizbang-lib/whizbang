// Template snippets for code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using System.Threading.Tasks;
using Whizbang.Generators.Templates.Placeholders;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for dispatcher code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class DispatcherSnippets {
  // Placeholder property to make snippets compile
  protected IServiceProvider _serviceProvider => null!;

  /// <summary>
  /// Example method showing snippet structure for Send routing.
  /// The actual snippets are extracted from #region blocks.
  /// </summary>
  protected ReceptorInvoker<TResult>? SendRoutingExample<TResult>(object message, Type messageType) {
    #region SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      var receptor = _serviceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      if (receptor == null) {
        return null;
      }

      [System.Diagnostics.DebuggerStepThrough]
      ValueTask<TResult> InvokeReceptor(object msg) {
        var typedMsg = (__MESSAGE_TYPE__)msg;
        var task = receptor.HandleAsync(typedMsg);

        // Fast path: Avoid async state machine for synchronously-completed tasks
        if (task.IsCompletedSuccessfully) {
          return new ValueTask<TResult>((TResult)(object)task.Result!);
        }

        // Slow path: Await asynchronously-completing tasks
        return AwaitAndCast(task);

        async ValueTask<TResult> AwaitAndCast(ValueTask<__RESPONSE_TYPE__> t) {
          var result = await t;
          return (TResult)(object)result!;
        }
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for Publish routing.
  /// The actual snippets are extracted from #region blocks.
  /// </summary>
  protected ReceptorPublisher<TEvent> PublishRoutingExample<TEvent>(TEvent @event, Type eventType) {
    #region PUBLISH_ROUTING_SNIPPET
    if (eventType == typeof(__MESSAGE_TYPE__)) {
      var receptors = _serviceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, object>>();

      [System.Diagnostics.DebuggerStepThrough]
      async Task PublishToReceptors(TEvent evt) {
        var typedEvt = (__MESSAGE_TYPE__)(object)evt!;
        foreach (var receptor in receptors) {
          await receptor.HandleAsync(typedEvt);
        }
      }

      return PublishToReceptors;
    }
    #endregion

    return _ => Task.CompletedTask;
  }

  /// <summary>
  /// Example method showing snippet structure for receptor registration.
  /// </summary>
  public void ReceptorRegistrationExample(IServiceCollection services) {
    #region RECEPTOR_REGISTRATION_SNIPPET
    services.AddTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void receptor registration.
  /// </summary>
  public void VoidReceptorRegistrationExample(IServiceCollection services) {
    #region VOID_RECEPTOR_REGISTRATION_SNIPPET
    services.AddTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void receptor routing.
  /// </summary>
  protected VoidReceptorInvoker? VoidSendRoutingExample(object message, Type messageType) {
    #region VOID_SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      var receptor = _serviceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      if (receptor == null) {
        return null;
      }

      [System.Diagnostics.DebuggerStepThrough]
      ValueTask InvokeReceptor(object msg) {
        var typedMsg = (__MESSAGE_TYPE__)msg;
        var task = receptor.HandleAsync(typedMsg);

        // Fast path: Avoid async state machine for synchronously-completed tasks
        if (task.IsCompletedSuccessfully) {
          return ValueTask.CompletedTask;
        }

        // Slow path: Await asynchronously-completing tasks
        return task;
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for diagnostic messages.
  /// </summary>
  public void DiagnosticMessageExample(System.Text.StringBuilder message) {
    #region DIAGNOSTIC_MESSAGE_SNIPPET
    message.AppendLine("  [__INDEX__] __RECEPTOR_NAME__");
    message.AppendLine("      Message:  __MESSAGE_NAME__");
    message.AppendLine("      Response: __RESPONSE_NAME__");
    #endregion
  }

  /// <summary>
  /// Header snippet for generated files.
  /// </summary>
  public void GeneratedFileHeader() {
    #region GENERATED_FILE_HEADER
    // <auto-generated/>
    // Generated at: __TIMESTAMP__
#nullable enable
    #endregion
  }
}
