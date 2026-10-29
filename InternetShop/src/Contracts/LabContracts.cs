namespace InternetShop.Api.Contracts;

public sealed record RaceDemoResponse(int CustomerId, ProductResponse Product);
public sealed record RaceStateResponse(int ProductId, int StockQuantity,
    long OrderedQuantity, IReadOnlyCollection<int> OrderIds);
