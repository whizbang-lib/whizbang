using System.Diagnostics.CodeAnalysis;
using Whizbang.Core;

namespace Whizbang.Core.Tests.Examples;

/// <summary>
/// Comprehensive examples demonstrating void receptor patterns (IReceptor&lt;TMessage&gt;).
/// Void receptors achieve zero allocations for command/event handling when results aren't needed.
/// </summary>
public static class VoidReceptorExamples {
  // ==========================================================================
  // EXAMPLE 1: Command Handler (Synchronous) - ZERO ALLOCATIONS
  // ==========================================================================

  /// <summary>
  /// Example command for processing user actions.
  /// Use void receptors when you only need side effects (logging, state changes, etc.)
  /// without returning business data.
  /// </summary>
  public record LogUserActionCommand(
    Guid UserId,
    string Action,
    Dictionary<string, string> Metadata
  );

  /// <summary>
  /// ZERO ALLOCATION PATTERN: Synchronous void receptor.
  /// Returns ValueTask.CompletedTask for immediate completion.
  /// </summary>
  public class LogUserActionReceptor : IReceptor<LogUserActionCommand> {
    private readonly ILogger _logger;

    public LogUserActionReceptor(ILogger logger) {
      _logger = logger;
    }

    public ValueTask HandleAsync(
        LogUserActionCommand message,
        CancellationToken cancellationToken = default) {
      // Perform synchronous work
      _logger.Log($"User {message.UserId} performed {message.Action}");

      // Zero allocation - return static singleton
      return ValueTask.CompletedTask;
    }
  }

  // ==========================================================================
  // EXAMPLE 2: Event Handler (Asynchronous I/O)
  // ==========================================================================

  /// <summary>
  /// Example event for notification delivery.
  /// </summary>
  public record SendNotificationEvent(
    Guid UserId,
    string Title,
    string Message,
    NotificationChannel Channel
  );

  public enum NotificationChannel {
    Email,
    SMS,
    Push
  }

  /// <summary>
  /// ASYNC PATTERN: Void receptor with asynchronous I/O.
  /// Use async when performing database writes, API calls, etc.
  /// </summary>
  public class SendNotificationReceptor : IReceptor<SendNotificationEvent> {
    private readonly INotificationService _notificationService;

    public SendNotificationReceptor(INotificationService notificationService) {
      _notificationService = notificationService;
    }

    public async ValueTask HandleAsync(
        SendNotificationEvent message,
        CancellationToken cancellationToken = default) {
      // Perform async I/O
      await _notificationService.SendAsync(
        message.UserId,
        message.Title,
        message.Message,
        message.Channel,
        cancellationToken
      );

      // No return value needed - notification sent is the result
    }
  }

  // ==========================================================================
  // EXAMPLE 3: State Management (Synchronous Update)
  // ==========================================================================

  /// <summary>
  /// Example command for updating cache.
  /// </summary>
  public record UpdateCacheCommand(
    string Key,
    object Value,
    TimeSpan? Expiration
  );

  /// <summary>
  /// STATE UPDATE PATTERN: Synchronous state changes with zero allocation.
  /// Perfect for caching, in-memory updates, metrics, etc.
  /// </summary>
  public class UpdateCacheReceptor : IReceptor<UpdateCacheCommand> {
    private readonly ICache _cache;
    private readonly IMetrics _metrics;

    public UpdateCacheReceptor(ICache cache, IMetrics metrics) {
      _cache = cache;
      _metrics = metrics;
    }

    public ValueTask HandleAsync(
        UpdateCacheCommand message,
        CancellationToken cancellationToken = default) {
      // Update cache
      if (message.Expiration.HasValue) {
        _cache.Set(message.Key, message.Value, message.Expiration.Value);
      } else {
        _cache.Set(message.Key, message.Value);
      }

      // Update metrics
      _metrics.Increment("cache.updates");

      // Zero allocation
      return ValueTask.CompletedTask;
    }
  }

  // ==========================================================================
  // EXAMPLE 4: Validation and Error Handling
  // ==========================================================================

  /// <summary>
  /// Example command with validation requirements.
  /// </summary>
  public record ProcessPaymentCommand(
    Guid OrderId,
    decimal Amount,
    string PaymentMethod,
    string Currency
  );

  /// <summary>
  /// VALIDATION PATTERN: Throw exceptions for validation errors.
  /// Void receptors can throw exceptions - dispatcher will propagate them.
  /// </summary>
  public class ProcessPaymentReceptor : IReceptor<ProcessPaymentCommand> {
    private readonly IPaymentGateway _gateway;

    public ProcessPaymentReceptor(IPaymentGateway gateway) {
      _gateway = gateway;
    }

    public async ValueTask HandleAsync(
        ProcessPaymentCommand message,
        CancellationToken cancellationToken = default) {
      // Validation - throw for errors
      if (message.Amount <= 0) {
        throw new ArgumentException("Payment amount must be positive", nameof(message.Amount));
      }

      if (string.IsNullOrWhiteSpace(message.PaymentMethod)) {
        throw new ArgumentException("Payment method is required", nameof(message.PaymentMethod));
      }

      // Process payment
      await _gateway.ProcessAsync(
        message.OrderId,
        message.Amount,
        message.PaymentMethod,
        message.Currency,
        cancellationToken
      );

      // Success - no return value needed
    }
  }

  // ==========================================================================
  // EXAMPLE 5: Fan-Out Pattern (Multiple Void Receptors)
  // ==========================================================================

  /// <summary>
  /// Example event that multiple receptors handle.
  /// </summary>
  public record OrderPlacedEvent(
    Guid OrderId,
    Guid CustomerId,
    decimal Total,
    DateTimeOffset PlacedAt
  );

  /// <summary>
  /// AUDIT RECEPTOR: Logs all order events for audit trail.
  /// </summary>
  public class AuditOrderReceptor : IReceptor<OrderPlacedEvent> {
    private readonly IAuditLog _auditLog;

    public AuditOrderReceptor(IAuditLog auditLog) {
      _auditLog = auditLog;
    }

    public ValueTask HandleAsync(
        OrderPlacedEvent message,
        CancellationToken cancellationToken = default) {
      _auditLog.Log($"Order {message.OrderId} placed for customer {message.CustomerId}");
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// ANALYTICS RECEPTOR: Tracks order metrics.
  /// </summary>
  public class AnalyticsOrderReceptor : IReceptor<OrderPlacedEvent> {
    private readonly IAnalytics _analytics;

    public AnalyticsOrderReceptor(IAnalytics analytics) {
      _analytics = analytics;
    }

    public ValueTask HandleAsync(
        OrderPlacedEvent message,
        CancellationToken cancellationToken = default) {
      _analytics.Track("order.placed", new {
        orderId = message.OrderId,
        total = message.Total,
        timestamp = message.PlacedAt
      });

      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// EMAIL RECEPTOR: Sends confirmation email.
  /// </summary>
  public class EmailOrderReceptor : IReceptor<OrderPlacedEvent> {
    private readonly IEmailService _emailService;

    public EmailOrderReceptor(IEmailService emailService) {
      _emailService = emailService;
    }

    public async ValueTask HandleAsync(
        OrderPlacedEvent message,
        CancellationToken cancellationToken = default) {
      await _emailService.SendOrderConfirmationAsync(
        message.CustomerId,
        message.OrderId,
        cancellationToken
      );
    }
  }

  // ==========================================================================
  // USAGE EXAMPLE: Dispatcher Integration
  // ==========================================================================

  /// <summary>
  /// Example showing how to use void receptors with the dispatcher.
  /// </summary>
  public class VoidReceptorUsageExample {
    private readonly IDispatcher _dispatcher;

    public VoidReceptorUsageExample(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    /// <summary>
    /// Example 1: Send command (void) - for commands that don't return results.
    /// </summary>
    public async Task SendCommandAsync() {
      var command = new LogUserActionCommand(
        UserId: Guid.NewGuid(),
        Action: "login",
        Metadata: new Dictionary<string, string> {
          ["ip"] = "192.168.1.1",
          ["userAgent"] = "Mozilla/5.0"
        }
      );

      // LocalInvokeAsync for void receptors - zero allocations!
      await _dispatcher.LocalInvokeAsync(command);

      // Command processed - no return value
    }

    /// <summary>
    /// Example 2: Publish event (void) - fan-out to multiple receptors.
    /// </summary>
    public async Task PublishEventAsync() {
      var @event = new OrderPlacedEvent(
        OrderId: Guid.NewGuid(),
        CustomerId: Guid.NewGuid(),
        Total: 150.00m,
        PlacedAt: DateTimeOffset.UtcNow
      );

      // PublishAsync sends to ALL receptors handling this event
      // In this example: AuditOrderReceptor, AnalyticsOrderReceptor, EmailOrderReceptor
      await _dispatcher.PublishAsync(@event);

      // All receptors processed the event
    }

    /// <summary>
    /// Example 3: Error handling with void receptors.
    /// </summary>
    public async Task HandleErrorsAsync() {
      var command = new ProcessPaymentCommand(
        OrderId: Guid.NewGuid(),
        Amount: -10.00m,  // Invalid!
        PaymentMethod: "credit_card",
        Currency: "USD"
      );

      try {
        await _dispatcher.LocalInvokeAsync(command);
      } catch (ArgumentException ex) {
        // Receptor threw validation exception
        Console.WriteLine($"Validation error: {ex.Message}");
      }
    }
  }

  // ==========================================================================
  // HELPER INTERFACES (Mocked for Examples)
  // ==========================================================================

  public interface ILogger {
    void Log(string message);
  }

  public interface INotificationService {
    Task SendAsync(
      Guid userId,
      string title,
      string message,
      NotificationChannel channel,
      CancellationToken ct = default
    );
  }

  public interface ICache {
    void Set(string key, object value);
    void Set(string key, object value, TimeSpan expiration);
  }

  public interface IMetrics {
    void Increment(string metric);
  }

  public interface IPaymentGateway {
    Task ProcessAsync(
      Guid orderId,
      decimal amount,
      string paymentMethod,
      string currency,
      CancellationToken ct = default
    );
  }

  public interface IAuditLog {
    void Log(string message);
  }

  public interface IAnalytics {
    void Track(string eventName, object properties);
  }

  public interface IEmailService {
    Task SendOrderConfirmationAsync(Guid customerId, Guid orderId, CancellationToken ct = default);
  }
}
