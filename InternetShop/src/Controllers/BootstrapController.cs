using InternetShop.Api.Contracts;
using InternetShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/bootstrap")]
public sealed class BootstrapController(ShopService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<BootstrapResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BootstrapResponse>> Get(CancellationToken cancellationToken)
    {
        return Ok(await service.GetBootstrapAsync(cancellationToken));
    }
}
