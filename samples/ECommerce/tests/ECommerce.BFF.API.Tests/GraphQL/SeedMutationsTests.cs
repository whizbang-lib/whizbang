using ECommerce.BFF.API.GraphQL;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Lenses;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace ECommerce.BFF.API.Tests.GraphQL;

/// <summary>
/// Unit tests for SeedMutations GraphQL mutation.
/// Uses manual test doubles (AOT-compatible) to isolate the SeedMutations logic from dependencies.
/// </summary>
public class SeedMutationsTests {
  /// <summary>
  /// Test double for IDispatcher that tracks SendAsync calls
  /// </summary>
  private class TestDispatcher : IDispatcher {
    public List<object> SentCommands { get; } = [];
    public int SendCount => SentCommands.Count;

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) {
      SentCommands.Add(message!);
      return Task.FromResult<IDeliveryReceipt>(
        DeliveryReceipt.Accepted(MessageId.New(), "test-dispatcher")
      );
    }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public Task PublishAsync<TEvent>(TEvent @event) => throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull {
      var receipts = new List<IDeliveryReceipt>();
      foreach (var message in messages) {
        SentCommands.Add(message);
        receipts.Add(DeliveryReceipt.Accepted(MessageId.New(), "test-dispatcher"));
      }
      return Task.FromResult<IEnumerable<IDeliveryReceipt>>(receipts);
    }
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotImplementedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotImplementedException();
  }

  /// <summary>
  /// Test double for IProductCatalogLens that returns configurable products
  /// </summary>
  private class TestProductCatalogLens : IProductCatalogLens {
    private readonly List<ProductDto> _products;

    public TestProductCatalogLens(List<ProductDto> products) {
      _products = products;
    }

    public Task<ProductDto?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) {
      return Task.FromResult<IReadOnlyList<ProductDto>>(_products);
    }
    public Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) {
      return Task.FromResult<IReadOnlyList<ProductDto>>(_products);
    }
  }

  /// <summary>
  /// Tests that SeedProducts dispatches 12 CreateProductCommands when no products exist.
  /// </summary>
  [Test]
  [Obsolete]
  public async Task SeedProducts_WhenNoProductsExist_Dispatches12CommandsAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var productLens = new TestProductCatalogLens(new List<ProductDto>());
    var logger = NullLogger<SeedMutations>.Instance;

    var sut = new SeedMutations(dispatcher, productLens, logger);

    // Act
    var result = await sut.SeedProductsAsync();

    // Assert - Verify 12 commands were dispatched
    await Assert.That(result).IsEqualTo(12);
    await Assert.That(dispatcher.SendCount).IsEqualTo(12);
    await Assert.That(dispatcher.SentCommands).HasCount().EqualTo(12);
    await Assert.That(dispatcher.SentCommands.All(c => c is CreateProductCommand)).IsTrue();
  }

  /// <summary>
  /// Tests that SeedProducts is idempotent - doesn't seed if products already exist.
  /// </summary>
  [Test]
  public async Task SeedProducts_WhenProductsAlreadyExist_ReturnsZeroAsync() {
    // Arrange
    var existingProduct = new ProductDto {
      ProductId = Guid.CreateVersion7(),
      Name = "Existing Product",
      Description = "Already exists",
      Price = 10m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = null,
      DeletedAt = null
    };

    var dispatcher = new TestDispatcher();
    var productLens = new TestProductCatalogLens(new List<ProductDto> { existingProduct });
    var logger = NullLogger<SeedMutations>.Instance;

    var sut = new SeedMutations(dispatcher, productLens, logger);

    // Act
    var result = await sut.SeedProductsAsync();

    // Assert - Verify no commands were dispatched
    await Assert.That(result).IsEqualTo(0);
    await Assert.That(dispatcher.SendCount).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that SeedProducts dispatches commands with correct product data.
  /// </summary>
  [Test]
  public async Task SeedProducts_DispatchesCommandsWithCorrectDataAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var productLens = new TestProductCatalogLens(new List<ProductDto>());
    var logger = NullLogger<SeedMutations>.Instance;

    var sut = new SeedMutations(dispatcher, productLens, logger);

    // Act
    await sut.SeedProductsAsync();

    // Assert - Verify all 12 commands have required data
    await Assert.That(dispatcher.SentCommands.Count).IsEqualTo(12);

    foreach (var command in dispatcher.SentCommands.Cast<CreateProductCommand>()) {
      // Verify all required fields are populated
      await Assert.That(command.ProductId).IsNotEqualTo(default(ProductId));
      await Assert.That(command.Name).IsNotNull();
      await Assert.That(command.Name).IsNotEmpty();
      await Assert.That(command.Description).IsNotNull();
      await Assert.That(command.Description).IsNotEmpty();
      await Assert.That(command.Price).IsGreaterThan(0m);
      await Assert.That(command.ImageUrl).IsNotNull();
      await Assert.That(command.InitialStock).IsGreaterThan(0);
    }

    // Verify first product data (Team Sweatshirt)
    var firstCommand = (CreateProductCommand)dispatcher.SentCommands[0];
    await Assert.That(firstCommand.Name).IsEqualTo("Team Sweatshirt");
    await Assert.That(firstCommand.Description).Contains("hoodie");
    await Assert.That(firstCommand.Price).IsEqualTo(45.99m);
    await Assert.That(firstCommand.ImageUrl).IsEqualTo("/images/sweatshirt.png");
    await Assert.That(firstCommand.InitialStock).IsEqualTo(75);
  }

  /// <summary>
  /// Tests that SeedProducts propagates exceptions from dispatcher.
  /// </summary>
  [Test]
  public async Task SeedProducts_WhenDispatcherFails_ThrowsExceptionAsync() {
    // Arrange - Create failing dispatcher
    var dispatcher = new FailingTestDispatcher();
    var productLens = new TestProductCatalogLens(new List<ProductDto>());
    var logger = NullLogger<SeedMutations>.Instance;

    var sut = new SeedMutations(dispatcher, productLens, logger);

    // Act & Assert - Verify exception is propagated
    await Assert.That(async () => await sut.SeedProductsAsync())
      .Throws<InvalidOperationException>()
      .WithMessage("Dispatcher failure");
  }

  /// <summary>
  /// Test double for IDispatcher that always throws exceptions
  /// </summary>
  private class FailingTestDispatcher : IDispatcher {
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) {
      throw new InvalidOperationException("Dispatcher failure");
    }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public Task PublishAsync<TEvent>(TEvent @event) => throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull {
      throw new InvalidOperationException("Dispatcher failure");
    }
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotImplementedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotImplementedException();
  }
}
