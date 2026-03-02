# Integration Tests

This directory contains integration tests that verify the complete flow between the Dispatcher and Receptors, testing them working together rather than in isolation.

## Purpose

While unit tests (`Dispatcher/DispatcherTests.cs` and `Receptors/ReceptorTests.cs`) test individual components in isolation with mocks, integration tests verify the actual end-to-end behavior of the system.

## Test Categories

### 1. Simple Command Flow
- **Test**: `Integration_SimpleCommandFlow_ShouldProcessCompletelyAsync`
- **Purpose**: Verifies basic command → dispatcher → receptor → response flow
- **Validates**: Complete message processing with real components

### 2. Sequential Message Processing
- **Test**: `Integration_SequentialMessages_ShouldProcessInOrderAsync`
- **Purpose**: Tests multiple messages processed in sequence
- **Validates**: Results from one message can be used in subsequent messages

### 3. Parallel Message Processing
- **Test**: `Integration_ParallelMessages_ShouldProcessConcurrentlyAsync`
- **Purpose**: Verifies concurrent message processing
- **Validates**: Independent messages can be processed simultaneously

### 4. Error Handling
- **Test**: `Integration_ReceptorValidationFailure_ShouldPropagateExceptionAsync`
- **Purpose**: Tests exception propagation through the complete stack
- **Validates**: Exceptions from receptors properly bubble up through dispatcher

### 5. Handler Not Found
- **Test**: `Integration_UnregisteredMessage_ShouldThrowHandlerNotFoundAsync`
- **Purpose**: Tests missing handler scenario
- **Validates**: Proper error when no receptor is registered for a message type

### 6. Context Propagation
- **Test**: `Integration_WithContext_ShouldPreserveContextThroughFlowAsync`
- **Purpose**: Verifies message context flows through entire pipeline
- **Validates**: CorrelationId and CausationId are preserved

### 7. Complete Workflow
- **Test**: `Integration_CompleteWorkflow_ShouldProcessMultiStepFlowAsync`
- **Purpose**: Simulates real-world multi-step workflow (user creation → welcome email)
- **Validates**: Complex business processes work end-to-end

### 8. Multiple Receptor Types
- **Test**: `Integration_MultipleReceptorTypes_ShouldRouteCorrectlyAsync`
- **Purpose**: Tests dispatcher routing to different receptor types
- **Validates**: Correct message routing with multiple registered receptors

### 9. Async Execution
- **Test**: `Integration_AsyncReceptorExecution_ShouldCompleteAsyncWorkAsync`
- **Purpose**: Verifies async receptor execution is handled properly
- **Validates**: True async execution (not synchronous blocking)

### 10. Service Provider Integration
- **Test**: `Integration_ServiceProvider_ShouldResolveAllDependenciesAsync`
- **Purpose**: Tests dependency injection and service resolution
- **Validates**: All components resolve correctly from DI container

## Test Receptors

The integration tests define their own receptor implementations to ensure complete control over the test scenarios:

- **OrderReceptor**: Handles order placement with validation and calculation
- **ShippingReceptor**: Processes shipping with address validation
- **PaymentReceptor**: Handles payment processing with amount validation
- **UserReceptor**: Manages user creation with email validation
- **EmailReceptor**: Sends welcome emails

## Running Integration Tests

```bash
# Run all integration tests
dotnet test --filter "Category=Integration"

# Run specific integration test
dotnet test --filter "FullyQualifiedName~Integration_SimpleCommandFlow"

# Run with detailed output
dotnet test --filter "Category=Integration" --verbosity detailed
```

## Key Differences from Unit Tests

| Aspect | Unit Tests | Integration Tests |
|--------|-----------|-------------------|
| **Scope** | Single component | Multiple components together |
| **Mocking** | Heavy use of mocks | Minimal/no mocking |
| **Dependencies** | Isolated | Real dependencies |
| **Purpose** | Verify component behavior | Verify system behavior |
| **Execution** | Fast | Slower (but still fast) |
| **Failures** | Pinpoint specific code | Indicate integration issues |

## Expected Behavior (TDD)

As part of our test-driven development approach:

1. **Currently**: All tests are expected to fail because implementation is incomplete
2. **During Implementation**: Tests will pass as dispatcher and receptor functionality is implemented
3. **Post-Implementation**: All tests should pass, providing confidence in the integration

## Coverage Goals

These integration tests ensure:

- ✅ Dispatcher correctly routes messages to receptors
- ✅ Receptors properly process messages and return results
- ✅ Error handling works throughout the stack
- ✅ Context is preserved across the pipeline
- ✅ Dependency injection works correctly
- ✅ Async execution is handled properly
- ✅ Multiple message types can be processed
- ✅ Sequential and parallel workflows both work
- ✅ Complex multi-step workflows function correctly
- ✅ Service provider integration is seamless

## Future Enhancements

Potential additions to integration tests:

- Tests with middleware/interceptors (when added)
- Performance benchmarks for message throughput
- Stress tests with high message volume
- Tests with event sourcing and projections (future features)
- Tests with sagas and process managers (future features)
