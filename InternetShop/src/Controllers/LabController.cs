using InternetShop.Api.Contracts;
using InternetShop.Api.Data;
using InternetShop.Api.Domain;
using InternetShop.Api.Exceptions;
using InternetShop.Api.Infrastructure;
using InternetShop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/lab")]
public sealed class LabController(ShopDbContext db, ShopService service, IOptions<LabOptions> options)
    : ControllerBase
{
    [HttpPost("prepare-race")]
    public async Task<ActionResult<RaceDemoResponse>> PrepareRace(CancellationToken ct)
    {
        if (!options.Value.Enabled) return NotFound();
        var customer = await service.GetBootstrapAsync(ct);
        var product = new Product
        {
            Sku = $"LAB2-RACE-{Guid.NewGuid():N}",
            Name = "Lab 2: last unit", Price = 1m, StockQuantity = 1
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return Ok(new RaceDemoResponse(customer.CustomerId,
            new ProductResponse(product.Id, product.Sku, product.Name, product.Price, product.StockQuantity)));
    }

    [HttpGet("products/{id:int}/state")]
    public async Task<ActionResult<RaceStateResponse>> State(int id, CancellationToken ct)
    {
        if (!options.Value.Enabled) return NotFound();
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (product is null) throw new ObjectMissingException($"Product '{id}' was not found.");
        var items = await db.OrderItems.AsNoTracking()
            .Where(x => x.ProductId == id && x.Order.Status != OrderStatus.Cancelled)
            .Select(x => new { x.OrderId, x.Quantity }).ToListAsync(ct);
        return Ok(new RaceStateResponse(id, product.StockQuantity,
            items.Sum(x => (long)x.Quantity), items.Select(x => x.OrderId).OrderBy(x => x).ToList()));
    }

    [HttpPost("fail")]
    public IActionResult Fail()
    {
        if (!options.Value.Enabled || !options.Value.EnableFailureInjection) return NotFound();
        throw new InvalidOperationException("Intentional Lab 2 processing failure.");
    }
}
