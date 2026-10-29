using InternetShop.Api.Contracts;
using InternetShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(ShopService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProductResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ProductResponse>>> GetAll(
        CancellationToken cancellationToken)
    {
        return Ok(await service.GetProductsAsync(cancellationToken));
    }
}
