using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/time")]
public sealed class TimeController : ControllerBase
{
    [HttpGet]
    public ActionResult<DateTimeOffset> GetServerTime()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(DateTimeOffset.Now);
    }
}
