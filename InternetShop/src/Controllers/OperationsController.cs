using System.Text.Json;
using InternetShop.Api.Contracts;
using InternetShop.Api.Exceptions;
using InternetShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/operations")]
public sealed class OperationsController(
    ShopService service, RaceOrderService race,
    SyncOrderService sync, SemaphoreOrderService semaphore) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost("{operation}")]
    public async Task<IActionResult> Execute(
        string operation,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = operation.Replace("-", "").Replace("_", "").ToLowerInvariant();
        return name switch
        {
            "racecreateorder" => Ok(await race.CreateOrderAsync(
                Read<CreateOrderRequest>(payload), cancellationToken)),
            "synccreateorder" => Ok(await sync.CreateOrderAsync(
                Read<CreateOrderRequest>(payload), cancellationToken)),
            "semaphorecreateorder" => Ok(await semaphore.CreateOrderAsync(
                Read<CreateOrderRequest>(payload), cancellationToken)),
            "transactioncreateorder" => Ok(await service.TransactionCreateOrderAsync(
                Read<CreateOrderRequest>(payload), cancellationToken)),
            "createorder" => Ok(await service.CreateOrderAsync(
                Read<CreateOrderRequest>(payload), cancellationToken)),
            "cancelorder" => Ok(await service.CancelOrderAsync(
                Read<CancelOrderRequest>(payload), cancellationToken)),
            "getproducts" => Ok(await service.GetProductsAsync(cancellationToken)),
            "payorder" => Ok(await service.PayOrderAsync(
                Read<PayOrderRequest>(payload), cancellationToken)),
            "payorderfail" => Ok(await service.PayOrderFailAsync(
                Read<PayOrderRequest>(payload), cancellationToken
            )),
            "payorderfailnt" => Ok(await service.PayOrderFailNoTranAsync(
                Read<PayOrderRequest>(payload), cancellationToken
            )),
            _ => throw new IncorrectOperationException($"Operation '{operation}' is not supported.")
        };
    }

    private static T Read<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(JsonOptions)
                ?? throw new IncorrectInputDataException("Request body is missing.");
        }
        catch (JsonException)
        {
            throw new IncorrectInputDataException("Request JSON contains incorrect data.");
        }
    }
}
