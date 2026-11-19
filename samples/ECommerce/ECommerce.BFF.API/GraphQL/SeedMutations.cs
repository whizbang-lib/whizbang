// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Lenses;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace ECommerce.BFF.API.GraphQL;

/// <summary>
/// GraphQL mutations for data seeding operations.
/// Used for development seeding and admin-triggered production seeding.
/// </summary>
public class SeedMutations(
  IDispatcher dispatcher,
  IProductCatalogLens productLens,
  ILogger<SeedMutations> logger) {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly IProductCatalogLens _productLens = productLens;
  private readonly ILogger<SeedMutations> _logger = logger;

  /// <summary>
  /// The number of products <see cref="SeedProductsAsync(CancellationToken)"/> seeds.
  /// </summary>
  public const int SEED_PRODUCT_COUNT = 12;

  /// <summary>
  /// Seeds 12 products matching the frontend mock data.
  /// Idempotent - skips seeding if products already exist.
  /// Dispatches CreateProductCommand to InventoryWorker via Service Bus.
  /// </summary>
  /// <returns>Number of products seeded</returns>
  public Task<int> SeedProductsAsync(CancellationToken cancellationToken = default) =>
    SeedProductsAsync([.. Enumerable.Range(0, SEED_PRODUCT_COUNT).Select(_ => Guid.CreateVersion7())], cancellationToken);

  /// <summary>
  /// Seeds the 12 products using caller-supplied product ids, in order, so the caller knows the
  /// seeded streams before the commands are dispatched (for example, to wait on exactly those
  /// streams' perspective processing).
  /// Idempotent - skips seeding if products already exist.
  /// </summary>
  /// <param name="productIds">Exactly <see cref="SEED_PRODUCT_COUNT"/> product ids.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of products seeded</returns>
  public async Task<int> SeedProductsAsync(IReadOnlyList<Guid> productIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(productIds);
    if (productIds.Count != SEED_PRODUCT_COUNT) {
      throw new ArgumentException($"Expected exactly {SEED_PRODUCT_COUNT} product ids, got {productIds.Count}.", nameof(productIds));
    }

    _logger.LogInformation("SeedProducts mutation called - checking if seeding is needed...");

    // Check if any products already exist (idempotency check)
    // Use GetAllAsync instead of checking specific IDs since UUIDv7 generates new IDs each time
    var existingProducts = await _productLens.GetAllAsync(includeDeleted: false, cancellationToken);

    if (existingProducts.Count > 0) {
      _logger.LogInformation(
        "Products already exist ({Count} found), skipping seed",
        existingProducts.Count);
      return 0;
    }

    // The 12 product ids, in seed order
    var prod1 = productIds[0];
    var prod2 = productIds[1];
    var prod3 = productIds[2];
    var prod4 = productIds[3];
    var prod5 = productIds[4];
    var prod6 = productIds[5];
    var prod7 = productIds[6];
    var prod8 = productIds[7];
    var prod9 = productIds[8];
    var prod10 = productIds[9];
    var prod11 = productIds[10];
    var prod12 = productIds[11];

    _logger.LogInformation("Seeding 12 products via CreateProductCommand...");

    // Seed all 12 products with stock levels matching frontend mocks
    var createProductCommands = new[] {
      new CreateProductCommand {
        ProductId = ProductId.From(prod1),
        Name = "Team Sweatshirt",
        Description = "Premium heavyweight hoodie with embroidered team logo and school colors",
        Price = 45.99m,
        ImageUrl = "/images/sweatshirt.png",
        InitialStock = 75
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod2),
        Name = "Team T-Shirt",
        Description = "Moisture-wicking performance tee with screen-printed team name",
        Price = 24.99m,
        ImageUrl = "/images/t-shirt.png",
        InitialStock = 120
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod3),
        Name = "Official Match Soccer Ball",
        Description = "Size 5 competition soccer ball with team logo, FIFA quality approved",
        Price = 34.99m,
        ImageUrl = "/images/soccer-ball.png",
        InitialStock = 45
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod4),
        Name = "Team Baseball Cap",
        Description = "Adjustable snapback cap with embroidered logo and moisture-wicking band",
        Price = 19.99m,
        ImageUrl = "/images/baseball-cap.png",
        InitialStock = 90
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod5),
        Name = "Foam #1 Finger",
        Description = "Giant foam finger in school colors - perfect for game day!",
        Price = 12.99m,
        ImageUrl = "/images/foam-finger.png",
        InitialStock = 150
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod6),
        Name = "Team Golf Umbrella",
        Description = "62-inch vented canopy umbrella with team colors and logo",
        Price = 29.99m,
        ImageUrl = "/images/umbrella.png",
        InitialStock = 35
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod7),
        Name = "Portable Stadium Seat",
        Description = "Padded bleacher cushion with backrest in team colors",
        Price = 32.99m,
        ImageUrl = "/images/bleacher-seat.png",
        InitialStock = 60
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod8),
        Name = "Team Beanie",
        Description = "Warm knit beanie with embroidered team logo for cold game days",
        Price = 16.99m,
        ImageUrl = "/images/beanie.png",
        InitialStock = 85
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod9),
        Name = "Team Scarf",
        Description = "Knitted supporter scarf with team name and colors - 60 inches long",
        Price = 22.99m,
        ImageUrl = "/images/scarf.png",
        InitialStock = 70
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod10),
        Name = "Water Bottle",
        Description = "32oz insulated stainless steel bottle with team logo",
        Price = 27.99m,
        ImageUrl = "/images/bottle.png",
        InitialStock = 100
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod11),
        Name = "Team Pennant",
        Description = "Felt pennant flag 12x30 inches - perfect for bedroom or locker decoration",
        Price = 14.99m,
        ImageUrl = "/images/pennant.png",
        InitialStock = 125
      },
      new CreateProductCommand {
        ProductId = ProductId.From(prod12),
        Name = "Team Drawstring Bag",
        Description = "Lightweight cinch sack with zippered pocket - great for gym or practice",
        Price = 18.99m,
        ImageUrl = "/images/drawstring-bag.png",
        InitialStock = 95
      }
    };

    // Dispatch all create product commands in a single batch
    // This uses SendManyAsync which creates ONE scope and flushes ONCE
    // instead of creating 12 scopes and 12 flushes (massive performance improvement)
    try {
      _logger.LogInformation("Dispatching {Count} CreateProductCommands in a single batch...", createProductCommands.Length);

      var receipts = await _dispatcher.SendManyAsync<CreateProductCommand>(createProductCommands);
      var seededCount = receipts.Count();

      _logger.LogInformation("Product seeding complete - dispatched {Count} commands in batch", seededCount);

      // Log individual product details
      for (int i = 0; i < createProductCommands.Length; i++) {
        var command = createProductCommands[i];
        _logger.LogInformation(
          "Dispatched CreateProductCommand for {ProductId} ({Name}) with {Stock} units",
          command.ProductId,
          command.Name,
          command.InitialStock);
      }

      return seededCount;
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to dispatch batch of CreateProductCommands");
      throw;
    }
  }
}
