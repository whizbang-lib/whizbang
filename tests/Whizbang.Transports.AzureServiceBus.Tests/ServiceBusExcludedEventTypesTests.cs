// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The excluded-event-types rule (#2175): its SQL form, and the idempotent reconcile that converges an event
/// subscription on its opt-out list without ever leaving the subscription without a rule that delivers.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceBusEntityProvisioning.cs</code-under-test>
public class ServiceBusExcludedEventTypesTests {
  private const string TOPIC = "myapp.orders.events";
  private const string SUBSCRIPTION = "orderservice-myapp.orders.events";
  private const string RULE = ServiceBusEntityProvisioning.EXCLUDED_EVENT_TYPES_RULE_NAME;

  private static readonly string[] _subjects = ["myapp.orders.events.orderpinged", "myapp.orders.events.outer+orderviewed"];
  private static readonly string _expression = ServiceBusEntityProvisioning.BuildExcludedEventTypesExpression(_subjects);

  // ========================================
  // EXPRESSION
  // ========================================

  [Test]
  public async Task BuildExpression_Subjects_NotInWithANullGuardAsync() {
    await Assert.That(_expression).IsEqualTo(
      "sys.Label IS NULL OR NOT (sys.Label IN ('myapp.orders.events.orderpinged', 'myapp.orders.events.outer+orderviewed'))");
  }

  [Test]
  public async Task BuildExpression_QuoteInASubject_IsDoubledAsync() {
    await Assert.That(ServiceBusEntityProvisioning.BuildExcludedEventTypesExpression(["it's"]))
      .IsEqualTo("sys.Label IS NULL OR NOT (sys.Label IN ('it''s'))");
  }

  // ========================================
  // NOTHING EXCLUDED
  // ========================================

  [Test]
  public async Task Apply_NothingExcluded_NeverExcluded_TouchesNothingAsync() {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingLogger();

    await _applyAsync(admin, [], logger);

    await Assert.That(admin.Writes).IsEmpty()
      .Because("a service that excludes nothing keeps exactly its $Default rule, with no churn");
    await Assert.That(admin.RuleNames).IsEquivalentTo(["$Default"]);
    await Assert.That(logger.Has(LogLevel.Information, "every event")).IsTrue()
      .Because("the effective rule is logged at startup");
  }

  [Test]
  public async Task Apply_NothingExcluded_PreviouslyExcluded_ReplacesTheRuleInPlaceWithMatchAllAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new SqlRuleFilter(_expression)));

    await _applyAsync(admin, []);

    await Assert.That(admin.Writes).IsEquivalentTo([$"update {RULE}"]);
    await Assert.That(admin.Rules.Single().Filter).IsTypeOf<TrueRuleFilter>();
    await Assert.That(admin.NeverWithoutARule).IsTrue();
  }

  [Test]
  public async Task Apply_NothingExcluded_RuleAlreadyMatchAll_TouchesNothingAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new TrueRuleFilter()));

    await _applyAsync(admin, []);

    await Assert.That(admin.Writes).IsEmpty();
  }

  [Test]
  public async Task Apply_NothingExcluded_RuleIsSqlOneEqualsOne_TouchesNothingAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new SqlRuleFilter("1=1")));

    await _applyAsync(admin, []);

    await Assert.That(admin.Writes).IsEmpty();
  }

  [Test]
  public async Task Apply_NothingExcluded_RuleHasAnotherFilterKind_ReplacesItWithMatchAllAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new CorrelationRuleFilter { Subject = "x" }));

    await _applyAsync(admin, []);

    await Assert.That(admin.Writes).IsEquivalentTo([$"update {RULE}"]);
    await Assert.That(admin.Rules.Single().Filter).IsTypeOf<TrueRuleFilter>();
  }

  // ========================================
  // TYPES EXCLUDED
  // ========================================

  [Test]
  public async Task Apply_Excluded_RuleHasAnotherFilterKind_ReplacesItInPlaceAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new CorrelationRuleFilter { Subject = "x" }));

    await _applyAsync(admin, _subjects);

    await Assert.That(admin.Writes).IsEquivalentTo([$"update {RULE}"]);
    await Assert.That(((SqlRuleFilter)admin.Rules.Single().Filter).SqlExpression).IsEqualTo(_expression);
  }

  [Test]
  public async Task Apply_Excluded_FromDefault_CreatesTheRuleThenRemovesDefaultAsync() {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(admin.Writes).IsEquivalentTo([$"create {RULE}", "delete $Default"]);
    await Assert.That(admin.Writes[0]).IsEqualTo($"create {RULE}").Because("the filter exists before $Default goes");
    await Assert.That(((SqlRuleFilter)admin.Rules.Single().Filter).SqlExpression).IsEqualTo(_expression);
    await Assert.That(admin.NeverWithoutARule).IsTrue();
    await Assert.That(logger.Has(LogLevel.Information, _expression)).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_AlreadyConverged_TouchesNothingAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new SqlRuleFilter(_expression)));
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(admin.Writes).IsEmpty();
    await Assert.That(logger.Has(LogLevel.Information, _expression)).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_DifferentList_ReplacesTheRuleInPlaceAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new SqlRuleFilter("sys.Label IS NULL OR NOT (sys.Label IN ('old'))")));

    await _applyAsync(admin, _subjects);

    await Assert.That(admin.Writes).IsEquivalentTo([$"update {RULE}"]);
    await Assert.That(((SqlRuleFilter)admin.Rules.Single().Filter).SqlExpression).IsEqualTo(_expression);
  }

  [Test]
  public async Task Apply_Excluded_RuleMatchAllAndDefaultPresent_UpdatesThenRemovesDefaultAsync() {
    var admin = new StatefulRuleAdminClient(_rule(RULE, new TrueRuleFilter()), _default());

    await _applyAsync(admin, _subjects);

    await Assert.That(admin.Writes).IsEquivalentTo([$"update {RULE}", "delete $Default"]);
    await Assert.That(admin.NeverWithoutARule).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_CreateRacesAnotherInstance_UpdatesTheRuleItCreatedAsync() {
    // Another instance created the rule between this instance's read and its create.
    var admin = new StatefulRuleAdminClient(_default()) {
      CreateConflictRule = _rule(RULE, new SqlRuleFilter("sys.Label IS NULL OR NOT (sys.Label IN ('other'))"))
    };

    await _applyAsync(admin, _subjects);

    await Assert.That(admin.Writes).IsEquivalentTo([$"create {RULE}", $"update {RULE}", "delete $Default"]);
    await Assert.That(((SqlRuleFilter)admin.Rules.Single().Filter).SqlExpression).IsEqualTo(_expression);
  }

  [Test]
  public async Task Apply_Excluded_DefaultAlreadyRemovedByAnotherInstance_IsToleratedAsync() {
    var admin = new StatefulRuleAdminClient(_default()) { DeleteNotFound = true };
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(logger.Has(LogLevel.Warning, "Failed")).IsFalse();
    await Assert.That(admin.Rules.Single().Name).IsEqualTo(RULE);
  }

  [Test]
  public async Task Apply_Excluded_ForeignRuleRemains_WarnsThatItCanStillDeliverAsync() {
    var admin = new StatefulRuleAdminClient(_default(), _rule("OpsRule", new SqlRuleFilter("1=1")));
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(admin.RuleNames).IsEquivalentTo([RULE, "OpsRule"]);
    await Assert.That(logger.Has(LogLevel.Warning, "OpsRule")).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_ExpressionOverTheBrokerLimit_LogsAnErrorAndTouchesNothingAsync() {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingLogger();
    var many = Enumerable.Range(0, 60).Select(i => $"myapp.orders.events.someveryverboseeventname{i:D3}").ToArray();

    await _applyAsync(admin, many, logger);

    await Assert.That(admin.Writes).IsEmpty();
    await Assert.That(admin.RuleNames).IsEquivalentTo(["$Default"]);
    await Assert.That(logger.Has(LogLevel.Error, "1024")).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_CreateFails_LogsAWarningAndKeepsDefaultAsync() {
    var admin = new StatefulRuleAdminClient(_default()) { CreateException = new RequestFailedException(500, "boom") };
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(admin.RuleNames).IsEquivalentTo(["$Default"]);
    await Assert.That(logger.Has(LogLevel.Warning, "Failed")).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_DeleteFailsWithAnotherStatus_LogsAWarningAsync() {
    var admin = new StatefulRuleAdminClient(_default()) { DeleteException = new RequestFailedException(500, "boom") };
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(logger.Has(LogLevel.Warning, "Failed")).IsTrue();
    await Assert.That(admin.NeverWithoutARule).IsTrue();
  }

  [Test]
  public async Task Apply_Excluded_CreateFailsWithAnotherConflictStatus_LogsAWarningAsync() {
    var admin = new StatefulRuleAdminClient(_default()) { CreateException = new RequestFailedException(400, "bad filter") };
    var logger = new RecordingLogger();

    await _applyAsync(admin, _subjects, logger);

    await Assert.That(logger.Has(LogLevel.Warning, "Failed")).IsTrue();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static Task _applyAsync(StatefulRuleAdminClient admin, IReadOnlyList<string> subjects, ILogger? logger = null) =>
    ServiceBusEntityProvisioning.ApplyExcludedEventTypesAsync(
      admin, TOPIC, SUBSCRIPTION, subjects, logger ?? new RecordingLogger(), CancellationToken.None);

  private static RuleProperties _default() => _rule("$Default", new TrueRuleFilter());

  private static RuleProperties _rule(string name, RuleFilter filter) => ServiceBusModelFactory.RuleProperties(name, filter);

  /// <summary>A logger recording every entry, every level enabled.</summary>
  private sealed class RecordingLogger : ILogger {
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _entries.Add((logLevel, formatter(state, exception)));

    public bool Has(LogLevel level, string fragment) =>
      _entries.Exists(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
  }
}

/// <summary>
/// An admin client keeping one subscription's rules as state, recording every write and whether the subscription was
/// ever left with no rule (a window in which the broker delivers nothing), with failure injection per operation.
/// </summary>
internal sealed class StatefulRuleAdminClient(params RuleProperties[] rules) : IServiceBusAdminClient {
  public List<RuleProperties> Rules { get; } = [.. rules];
  public List<string> Writes { get; } = [];
  public bool NeverWithoutARule { get; private set; } = true;
  public int GetRulesCalls { get; private set; }

  public IEnumerable<string> RuleNames => Rules.Select(r => r.Name);

  public Exception? CreateException { get; init; }
  public Exception? DeleteException { get; init; }

  /// <summary>When set, the first create answers 409 after this rule appears, as if another instance created it.</summary>
  public RuleProperties? CreateConflictRule { get; set; }

  /// <summary>When true, a delete answers 404 after removing the rule, as if another instance removed it first.</summary>
  public bool DeleteNotFound { get; init; }

  public async IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    GetRulesCalls++;
    await Task.CompletedTask;
    foreach (var rule in Rules.ToList()) {
      yield return rule;
    }
  }

  public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options,
      CancellationToken cancellationToken = default) {
    Writes.Add($"create {options.Name}");
    if (CreateException is not null) {
      return Task.FromException(CreateException);
    }
    if (CreateConflictRule is { } conflict) {
      CreateConflictRule = null;
      Rules.Add(conflict);
      return Task.FromException(new RequestFailedException(409, "exists"));
    }
    Rules.Add(ServiceBusModelFactory.RuleProperties(options.Name, options.Filter));
    return Task.CompletedTask;
  }

  public Task UpdateRuleAsync(string topicName, string subscriptionName, RuleProperties rule,
      CancellationToken cancellationToken = default) {
    Writes.Add($"update {rule.Name}");
    var index = Rules.FindIndex(r => r.Name == rule.Name);
    Rules[index] = ServiceBusModelFactory.RuleProperties(rule.Name, rule.Filter);
    return Task.CompletedTask;
  }

  public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName,
      CancellationToken cancellationToken = default) {
    Writes.Add($"delete {ruleName}");
    if (DeleteException is not null) {
      return Task.FromException(DeleteException);
    }
    Rules.RemoveAll(r => r.Name == ruleName);
    NeverWithoutARule &= Rules.Count > 0;
    return DeleteNotFound ? Task.FromException(new RequestFailedException(404, "gone")) : Task.CompletedTask;
  }

  public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<NamespaceProperties>(null!);
  public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) => Task.FromResult(true);
  public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromResult(true);
  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration,
      CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount,
      TimeSpan lockDuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration,
      CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName,
      CancellationToken cancellationToken = default) =>
    Task.FromResult(ServiceBusModelFactory.SubscriptionProperties(
      topicName: topicName,
      subscriptionName: subscriptionName,
      lockDuration: TimeSpan.FromMinutes(5),
      requiresSession: false,
      defaultMessageTimeToLive: TimeSpan.FromDays(14),
      autoDeleteOnIdle: TimeSpan.FromDays(30),
      maxDeliveryCount: 10,
      userMetadata: string.Empty));
  public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;
  public Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName,
      CancellationToken cancellationToken = default) => Task.FromResult(0L);
}
