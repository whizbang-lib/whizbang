using ECommerce.Contracts.Commands;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Services;

/// <summary>
/// Hosted service that seeds 12 products matching the frontend mock data on application startup.
/// Idempotent - skips seeding if products already exist.
/// </summary>
public class ProductSeedService(
  IDispatcher dispatcher,
  IProductLens productLens,
  ILogger<ProductSeedService> logger) : IHostedService {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly IProductLens _productLens = productLens;
  private readonly ILogger<ProductSeedService> _logger = logger;

  public async Task StartAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("ProductSeedService: Checking if seeding is needed...");

    // Generate deterministic UUIDv7 IDs for the 12 products
    var prod1 = Guid.CreateVersion7();
    var prod2 = Guid.CreateVersion7();
    var prod3 = Guid.CreateVersion7();
    var prod4 = Guid.CreateVersion7();
    var prod5 = Guid.CreateVersion7();
    var prod6 = Guid.CreateVersion7();
    var prod7 = Guid.CreateVersion7();
    var prod8 = Guid.CreateVersion7();
    var prod9 = Guid.CreateVersion7();
    var prod10 = Guid.CreateVersion7();
    var prod11 = Guid.CreateVersion7();
    var prod12 = Guid.CreateVersion7();

    // Check if any of the 12 products already exist (idempotency check)
    var productIds = new[] {
      prod1, prod2, prod3, prod4,
      prod5, prod6, prod7, prod8,
      prod9, prod10, prod11, prod12
    };

    var existingProducts = await _productLens.GetByIdsAsync(productIds, cancellationToken);

    if (existingProducts.Count > 0) {
      _logger.LogInformation(
        "ProductSeedService: Products already exist ({Count} found), skipping seed",
        existingProducts.Count);
      return;
    }

    _logger.LogInformation("ProductSeedService: Seeding 12 products...");

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

    // Dispatch all create product commands sequentially
    foreach (var command in createProductCommands) {
      try {
        await _dispatcher.SendAsync(command);
        _logger.LogInformation(
          "ProductSeedService: Created product {ProductId} ({Name}) with {Stock} units",
          command.ProductId,
          command.Name,
          command.InitialStock);
      } catch (Exception ex) {
        _logger.LogError(ex,
          "ProductSeedService: Failed to create product {ProductId}",
          command.ProductId);
        throw;
      }
    }

    _logger.LogInformation("ProductSeedService: Product seeding complete");
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    // No cleanup needed
    return Task.CompletedTask;
  }
}
