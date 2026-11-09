namespace InternetShop.Messaging;

public static class ShopTopology
{
    public const string AuditQueue = "internetshop.audit";
    public const string NotificationQueue = "internetshop.notifications";
    public const string InventoryQueue = "internetshop.inventory";
    public const string AnalyticsQueue = "internetshop.analytics";

    public const string OrderCreatedKey = "internetshop.order.created";
    public const string OrderCancelledKey = "internetshop.order.cancelled";
    public const string PaymentCompletedKey = "internetshop.payment.completed";
    public const string StockUpdatedKey = "internetshop.stock.updated";
}
