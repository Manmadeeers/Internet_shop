using InternetShop.Api.Contracts;
using InternetShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(ShopService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<OrderResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<OrderResponse>>> GetAll(
        CancellationToken cancellationToken)
    {
        return Ok(await service.GetOrdersAsync(cancellationToken));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponse>> Get(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await service.GetOrderAsync(id, cancellationToken));
    }
}
