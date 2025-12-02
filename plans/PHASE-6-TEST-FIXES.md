# Phase 6 - Test Failure Investigation and Fix Plan

## Problem Statement - **CORRECTED WITH BASELINE DATA**

**IMPORTANT FINDING**: The 44 Postgres test failures existed BEFORE Phase 6 work began. Our Phase 6 changes actually IMPROVED the situation!

### Baseline Analysis Complete

**Baseline (commit 97c5622 - BEFORE Phase 6 WorkCoordinator tests)**:
- Total: 1,895 tests
- Failed: **64**
- Postgres failures: **59**
- Other failures: 5 (Core: 1, Generators: 3, Data: 1)

**After Schema Alignment (commit 03c6156)**:
- Total: 1,903 tests (+8 new tests)
- Failed: **49** ✅ **IMPROVED by 15 tests**
- Postgres failures: **44** ✅ **IMPROVED by 15 tests**
- Other failures: 5 (same as baseline)

**Current (commit 9eeda93 - AFTER ambiguous column fixes)**:
- Total: 341 Postgres tests
- Failed: **20** ✅ **IMPROVED by 24 more tests (55% improvement)**
- Postgres failures: **20** ✅ **Down from 44**
- Status: 321 passing, 0 skipped

### Key Findings

1. **We did NOT break these tests** - They were failing before we started
2. **We actually fixed 15 Postgres tests** - Our schema alignment work resolved schema mismatch issues
3. **The remaining 44 failures are pre-existing** - Likely Testcontainers infrastructure issues from earlier commits
4. **Non-Postgres failures unchanged** - Core, Generators, and Data test failures are same as baseline

### Fixes Applied (Commit 9eeda93)

**Major Schema Alignment Fixes:**
1. ✅ Fixed outbox column names (`event_type`/`event_data` vs `message_type`/`message_data`)
2. ✅ Fixed `process_work_batch` parameter name typo (`p_inbox_completed_ids`)
3. ✅ Renamed RETURNS TABLE column from `message_id` to `msg_id` (eliminated ambiguity)
4. ✅ Added table aliases to ALL UPDATE statements in function
5. ✅ Restructured orphaned message CTEs with FOR UPDATE SKIP LOCKED

**Impact**: 44 failures → 20 failures (55% improvement, 24 tests fixed)

**Tests Now Passing:**
- ✅ All 8 DapperWorkCoordinator tests (heartbeat, completions, failures, orphaned recovery, mixed ops)
- ✅ All outbox/inbox message marking tests
- ✅ All ambiguous column reference errors eliminated

### Current Status (Commit 9eeda93)

**Postgres Tests** (20 failures remaining):
- **11 Sequence tests**: `column "sequence_key" does not exist` (schema has `sequence_name`)
- **5 Request/Response tests**: `column "response_envelope" does not exist` (schema has separate columns)
- **1 Event store test**: `"whizbang_event_store" is not a table` (view usage issue)
- **1 Core test**: HandlerNotFoundException (pre-existing, not Postgres-related)
- **2 other tests**: Need investigation

**Other Project Failures** (unchanged from baseline):
- **Whizbang.Generators.Tests**: 3 failures
- **Whizbang.Data.Tests**: 1 failure
- **Whizbang.Core.Tests**: 1 failure (same as above)

## Test Failure Summary

### Current Status (as of 2025-12-02 - Commit 9eeda93)
```
Postgres Tests:
Total: 341 tests
Passed: 321
Failed: 20
Skipped: 0
Duration: ~25 seconds
```

### Breakdown by Failure Type

#### 1. Sequence Tests (11 failures)
**Error**: `column "sequence_key" does not exist`
**Root Cause**: Schema uses `sequence_name`, code uses `sequence_key`
**Tests Affected**: DapperPostgresSequenceStoreTests
**Fix**: Update Dapper queries to use `sequence_name` column

#### 2. Request/Response Tests (5 failures)
**Error**: `column "response_envelope" does not exist`
**Root Cause**: Schema has `response_type` and `response_data`, code expects single `response_envelope`
**Tests Affected**: DapperPostgresRequestResponseStoreTests
**Fix**: Update Dapper queries to use correct column names

#### 3. Event Store View Test (1 failure)
**Error**: `"whizbang_event_store" is not a table`
**Root Cause**: Code trying to use legacy view as a table
**Tests Affected**: Event store query tests
**Fix**: Use `wb_event_store` table directly instead of view

#### 4. Other Tests (3 failures)
**Error**: Various (need investigation)
**Tests Affected**: TBD
**Fix**: Investigate individually

#### 2. Whizbang.Generators.Tests (3 failures)
**Error Pattern**: TBD - need to examine specific test output

#### 3. Whizbang.Data.Tests (1 failure)
**Error Pattern**: TBD - need to examine specific test output

#### 4. Whizbang.Core.Tests (1 failure)
**Test**: `SendAsync_NoLocalReceptor_WithOutbox_RoutesToOutboxAsync`
**Error**: `HandlerNotFoundException: No handler found for message type 'CreateProductCommand'`
**Root Cause**: Expected behavior change or missing test setup

## Investigation Plan

### Phase 1: Analyze Root Causes (1-2 hours)

#### Step 1: Document Baseline
- [ ] Checkout commit before Phase 6 changes (before commit `19b2be6`)
- [ ] Run full test suite and capture results
- [ ] Document which tests were passing/failing at baseline

#### Step 2: Identify Breaking Changes
- [ ] List all schema changes made in Phase 6
- [ ] List all code changes to Inbox/Outbox/WorkCoordinator
- [ ] Compare before/after test configurations

#### Step 3: Categorize Failures
For each failing test:
- [ ] Capture full error message and stack trace
- [ ] Identify immediate cause (connection, schema, logic, etc.)
- [ ] Group by root cause category

### Phase 2: Fix Testcontainers Issues (Postgres Tests)

#### Investigation Tasks
- [ ] Verify PostgresTestBase.SetupAsync() is being called
- [ ] Check if Testcontainers PostgreSQL image is pulling correctly
- [ ] Verify schema SQL file path resolution
- [ ] Add diagnostic logging to PostgresTestBase
- [ ] Check for async initialization issues

#### Potential Fixes

**Fix 1: Add Explicit Database Creation**
```csharp
private async Task InitializeDatabaseAsync() {
  using var connection = await _connectionFactory!.CreateConnectionAsync();

  // Explicitly create database if needed
  var createDbSql = "CREATE DATABASE IF NOT EXISTS whizbang_test;";
  using var createCmd = connection.CreateCommand();
  createCmd.CommandText = createDbSql;
  await createCmd.ExecuteNonQueryAsync();

  // Continue with schema initialization...
}
```

**Fix 2: Verify Connection String Format**
```csharp
// Log connection string (redacted) for debugging
_logger.LogInformation("Connection string format: {Format}",
  RedactPassword(_postgresContainer.GetConnectionString()));
```

**Fix 3: Add Retry Logic for Container Startup**
```csharp
// Wait for container to be fully ready
await _postgresContainer.StartAsync();
await Task.Delay(TimeSpan.FromSeconds(2)); // Give DB time to initialize
```

**Fix 4: Verify Schema File Path**
```csharp
var schemaPath = Path.Combine(
  AppContext.BaseDirectory,
  "..", "..", "..", "..", "..",
  "src", "Whizbang.Data.Dapper.Postgres", "whizbang-schema.sql");

if (!File.Exists(schemaPath)) {
  throw new FileNotFoundException($"Schema file not found at: {schemaPath}");
}
```

### Phase 3: Fix Core Tests

#### Test: SendAsync_NoLocalReceptor_WithOutbox_RoutesToOutboxAsync

**Investigation**:
- [ ] Review test setup - is receptor intentionally missing?
- [ ] Check if Outbox fallback behavior changed in Phase 6
- [ ] Verify test expectations match current implementation

**Potential Fixes**:
- Update test to register receptor if required
- Update test expectations if behavior intentionally changed
- Fix Dispatcher logic if regression introduced

### Phase 4: Fix Generators Tests

#### Investigation Tasks
- [ ] Capture full error output for all 3 failures
- [ ] Identify if generator changes in Phase 6 affected tests
- [ ] Check for source generation issues

### Phase 5: Fix Data Tests

#### Investigation Tasks
- [ ] Capture full error output
- [ ] Identify specific failure cause
- [ ] Check if schema changes affected generic data tests

## Execution Order

### Priority 1: Testcontainers Infrastructure (Highest Impact)
Fix PostgresTestBase to resolve 44 test failures
- **Estimated Time**: 2-4 hours
- **Risk**: Medium - Testcontainers can be finicky
- **Impact**: Unblocks all Postgres integration tests

### Priority 2: Core Test (Dispatcher Behavior)
Fix SendAsync_NoLocalReceptor_WithOutbox_RoutesToOutboxAsync
- **Estimated Time**: 30 minutes - 1 hour
- **Risk**: Low - Single test, clear error message
- **Impact**: Verifies Dispatcher/Outbox integration

### Priority 3: Generators Tests
Fix 3 generator test failures
- **Estimated Time**: 1-2 hours
- **Risk**: Low-Medium
- **Impact**: Ensures source generation working correctly

### Priority 4: Data Test
Fix single data test failure
- **Estimated Time**: 30 minutes - 1 hour
- **Risk**: Low
- **Impact**: Verifies generic data layer

## Success Criteria

- [ ] All 1903 tests passing
- [ ] No test skips (unless intentional)
- [ ] Test execution time acceptable (< 2 minutes for unit tests)
- [ ] Postgres integration tests stable and repeatable
- [ ] Documentation updated with any test infrastructure changes

## Rollback Plan

If fixes prove too complex or introduce additional issues:
1. Revert to commit before Phase 6 changes
2. Re-implement Phase 6 with test-first approach
3. Fix tests incrementally as features are added

## Open Questions

1. Were there any environment changes between test runs?
2. Are there any Docker/Testcontainers version dependencies?
3. Do tests pass in CI/CD or only failing locally?
4. Are there any hidden dependencies on external services?

## Next Steps

1. **IMMEDIATE**: Run baseline comparison (checkout pre-Phase-6, run tests)
2. **NEXT**: Implement diagnostic logging in PostgresTestBase
3. **THEN**: Systematically fix each category of failures
4. **FINALLY**: Verify full test suite passes

---

## Next Actions (Remaining 20 Failures)

### Priority 1: Sequence Tests (11 failures) - Highest Impact
**Estimated Time**: 15-30 minutes
**Steps**:
1. Find DapperPostgresSequenceStore implementation
2. Update all queries to use `sequence_name` instead of `sequence_key`
3. Run tests to verify fix

### Priority 2: Request/Response Tests (5 failures)
**Estimated Time**: 15-30 minutes
**Steps**:
1. Find DapperPostgresRequestResponseStore implementation
2. Update queries to use `response_type` and `response_data` instead of `response_envelope`
3. Update mapping code to serialize/deserialize correctly
4. Run tests to verify fix

### Priority 3: Event Store View Test (1 failure)
**Estimated Time**: 10-15 minutes
**Steps**:
1. Find code using `whizbang_event_store` view
2. Change to use `wb_event_store` table directly
3. Run tests to verify fix

### Priority 4: Investigate Remaining 3 Tests
**Estimated Time**: 30 minutes - 1 hour
**Steps**:
1. Capture full error messages
2. Categorize by root cause
3. Fix individually

**Total Estimated Time**: 1-2 hours to complete all remaining Postgres tests

---

**Status**: IN PROGRESS - 24 tests fixed (55% improvement), 20 remaining
**Created**: 2025-12-02
**Last Updated**: 2025-12-02 (Post commit 9eeda93)
