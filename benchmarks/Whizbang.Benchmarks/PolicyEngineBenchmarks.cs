using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for policy engine.
/// Measures policy matching performance with varying number of policies.
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[MarkdownExporter]
public class PolicyEngineBenchmarks {
  private record OrderCommand(string OrderId, decimal Amount);
  private record PaymentCommand(string PaymentId, decimal Amount);
  private record NotificationCommand(string UserId, string Message);

  private IPolicyEngine _engine1Policy = null!;
  private IPolicyEngine _engine5Policies = null!;
  private IPolicyEngine _engine20Policies = null!;
  private PolicyContext _orderContext = null!;
  private PolicyContext _paymentContext = null!;

  [GlobalSetup]
  public void Setup() {
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();

    // Setup contexts
    var orderMessage = new OrderCommand("order-123", 100m);
    var orderEnvelope = CreateEnvelope(orderMessage);
    _orderContext = new PolicyContext(orderMessage, orderEnvelope, serviceProvider, "benchmark");

    var paymentMessage = new PaymentCommand("payment-456", 200m);
    var paymentEnvelope = CreateEnvelope(paymentMessage);
    _paymentContext = new PolicyContext(paymentMessage, paymentEnvelope, serviceProvider, "benchmark");

    // Engine with 1 policy
    _engine1Policy = new PolicyEngine();
    _engine1Policy.AddPolicy("OrderPolicy", ctx => ctx.Message is OrderCommand, cfg => cfg.UseTopic("orders"));

    // Engine with 5 policies
    _engine5Policies = new PolicyEngine();
    _engine5Policies.AddPolicy("Policy1", ctx => ctx.Message is NotificationCommand, cfg => cfg.UseTopic("notifications"));
    _engine5Policies.AddPolicy("Policy2", ctx => ctx.Message is PaymentCommand && ((PaymentCommand)ctx.Message).Amount > 1000, cfg => cfg.UseTopic("large-payments"));
    _engine5Policies.AddPolicy("Policy3", ctx => ctx.Message is PaymentCommand, cfg => cfg.UseTopic("payments"));
    _engine5Policies.AddPolicy("Policy4", ctx => ctx.Message is OrderCommand && ((OrderCommand)ctx.Message).Amount > 500, cfg => cfg.UseTopic("large-orders"));
    _engine5Policies.AddPolicy("Policy5", ctx => ctx.Message is OrderCommand, cfg => cfg.UseTopic("orders"));

    // Engine with 20 policies (stress test)
    _engine20Policies = new PolicyEngine();
    for (int i = 0; i < 19; i++) {
      _engine20Policies.AddPolicy($"Policy{i}", ctx => ctx.Message is NotificationCommand, cfg => cfg.UseTopic($"topic-{i}"));
    }
    _engine20Policies.AddPolicy("OrderPolicy", ctx => ctx.Message is OrderCommand, cfg => cfg.UseTopic("orders"));
  }

  private IMessageEnvelope CreateEnvelope<T>(T message) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Benchmark",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });
    return envelope;
  }

  [Benchmark(Baseline = true)]
  public async Task<PolicyConfiguration?> MatchPolicy_1Policy_FirstMatch() {
    return await _engine1Policy.MatchAsync(_orderContext);
  }

  [Benchmark]
  public async Task<PolicyConfiguration?> MatchPolicy_5Policies_LastMatch() {
    return await _engine5Policies.MatchAsync(_orderContext);
  }

  [Benchmark]
  public async Task<PolicyConfiguration?> MatchPolicy_5Policies_MiddleMatch() {
    return await _engine5Policies.MatchAsync(_paymentContext);
  }

  [Benchmark]
  public async Task<PolicyConfiguration?> MatchPolicy_20Policies_LastMatch() {
    return await _engine20Policies.MatchAsync(_orderContext);
  }

  [Benchmark]
  public async Task MatchPolicy_100Times_1Policy() {
    for (int i = 0; i < 100; i++) {
      await _engine1Policy.MatchAsync(_orderContext);
    }
  }

  [Benchmark]
  public async Task MatchPolicy_100Times_5Policies() {
    for (int i = 0; i < 100; i++) {
      await _engine5Policies.MatchAsync(_orderContext);
    }
  }

  [Benchmark]
  public async Task MatchPolicy_100Times_20Policies() {
    for (int i = 0; i < 100; i++) {
      await _engine20Policies.MatchAsync(_orderContext);
    }
  }
}
