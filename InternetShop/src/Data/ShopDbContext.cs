using InternetShop.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InternetShop.Api.Data;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("customer");
        customer.HasKey(x => x.Id);
        customer.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        customer.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        customer.Property(x => x.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
        customer.HasIndex(x => x.Email).IsUnique();

        var product = modelBuilder.Entity<Product>();
        product.ToTable("product");
        product.HasKey(x => x.Id);
        product.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        product.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(100).IsRequired();
        product.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        product.Property(x => x.Price).HasColumnName("price").HasPrecision(12, 2);
        product.Property(x => x.StockQuantity).HasColumnName("stockquantity");
        product.HasIndex(x => x.Sku).IsUnique();

        var order = modelBuilder.Entity<Order>();
        order.ToTable("orders");
        order.HasKey(x => x.Id);
        order.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        order.Property(x => x.CustomerId).HasColumnName("customerid");
        order.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>();
        order.Property(x => x.Total).HasColumnName("total").HasPrecision(12, 2);
        order.Property(x => x.CreatedAt).HasColumnName("createdat");
        order.Property(x => x.PaidAt).HasColumnName("paidat");
        order.Property(x => x.CancelledAt).HasColumnName("cancelledat");
        order.HasOne(x => x.Customer).WithMany(x => x.Orders)
            .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.NoAction);
        order.HasIndex(x => x.CustomerId);

        var orderItem = modelBuilder.Entity<OrderItem>();
        orderItem.ToTable("orderitem");
        orderItem.HasKey(x => new { x.OrderId, x.ProductId });
        orderItem.Property(x => x.OrderId).HasColumnName("orderid");
        orderItem.Property(x => x.ProductId).HasColumnName("productid");
        orderItem.Property(x => x.Quantity).HasColumnName("quantity");
        orderItem.Property(x => x.UnitPrice).HasColumnName("unitprice").HasPrecision(12, 2);
        orderItem.HasOne(x => x.Order).WithMany(x => x.Items)
            .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        orderItem.HasOne(x => x.Product).WithMany(x => x.OrderItems)
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.NoAction);
        orderItem.HasIndex(x => x.ProductId);

        var payment = modelBuilder.Entity<Payment>();
        payment.ToTable("payment");
        payment.HasKey(x => x.Id);
        payment.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        payment.Property(x => x.OrderId).HasColumnName("orderid");
        payment.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
        payment.Property(x => x.Method).HasColumnName("method").HasMaxLength(50).IsRequired();
        payment.Property(x => x.CompletedAt).HasColumnName("completedat");
        payment.HasOne(x => x.Order).WithOne(x => x.Payment)
            .HasForeignKey<Payment>(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        payment.HasIndex(x => x.OrderId).IsUnique();
    }
}
