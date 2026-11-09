CREATE SCHEMA IF NOT EXISTS internetshop_broker;

CREATE TABLE IF NOT EXISTS internetshop_broker.queues
(
    name text PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS internetshop_broker.bindings
(
    queue_name text NOT NULL
        REFERENCES internetshop_broker.queues(name),
    pattern text NOT NULL,
    PRIMARY KEY (queue_name, pattern)
);

CREATE TABLE IF NOT EXISTS internetshop_broker.messages
(
    event_id uuid PRIMARY KEY,
    routing_key text NOT NULL,
    envelope jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS internetshop_broker.deliveries
(
    queue_name text NOT NULL
        REFERENCES internetshop_broker.queues(name),
    event_id uuid NOT NULL
        REFERENCES internetshop_broker.messages(event_id),
    position bigint GENERATED ALWAYS AS IDENTITY,
    state text NOT NULL DEFAULT 'ready'
        CHECK (state IN ('ready', 'inflight', 'done', 'dead')),
    attempts integer NOT NULL DEFAULT 0,
    available_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    lease_token uuid,
    lease_until timestamptz,
    completed_at timestamptz,
    last_error text,
    PRIMARY KEY (queue_name, event_id)
);

CREATE INDEX IF NOT EXISTS ix_internetshop_broker_ready
    ON internetshop_broker.deliveries
    (queue_name, available_at, position)
    WHERE state = 'ready';

CREATE INDEX IF NOT EXISTS ix_internetshop_broker_expired
    ON internetshop_broker.deliveries (lease_until)
    WHERE state = 'inflight';

CREATE UNIQUE INDEX IF NOT EXISTS ix_internetshop_broker_receipt
    ON internetshop_broker.deliveries (lease_token)
    WHERE lease_token IS NOT NULL;

INSERT INTO internetshop_broker.queues(name)
VALUES
    ('internetshop.audit'),
    ('internetshop.notifications'),
    ('internetshop.inventory'),
    ('internetshop.analytics')
ON CONFLICT DO NOTHING;

INSERT INTO internetshop_broker.bindings(queue_name, pattern)
VALUES
    ('internetshop.audit', 'internetshop.#'),
    ('internetshop.notifications', 'internetshop.order.*'),
    ('internetshop.notifications', 'internetshop.payment.completed'),
    ('internetshop.inventory', 'internetshop.stock.updated'),
    ('internetshop.analytics', 'internetshop.#')
ON CONFLICT DO NOTHING;
