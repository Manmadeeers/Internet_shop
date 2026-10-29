INSERT INTO customer (name, email)
VALUES ('Demo Customer', 'demo@internetshop.local')
ON CONFLICT (email) DO NOTHING;

INSERT INTO product (sku, name, price, stockquantity)
VALUES
    ('PHONE-001', 'Smartphone', 699.00, 25),
    ('LAPTOP-001', 'Laptop', 1299.00, 15),
    ('HEADPHONES-001', 'Wireless Headphones', 149.00, 40),
    ('KEYBOARD-001', 'Mechanical Keyboard', 99.00, 30)
ON CONFLICT (sku) DO NOTHING;
