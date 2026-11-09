# Custom message brokers and subscribers

The Internet Shop uses two stateless HTTP broker instances backed by the same
PostgreSQL message store. Publishers and subscribers rotate across both broker
URLs and fail over when one endpoint is unavailable, times out, or returns an
HTTP 5xx response.

## Start everything

From `InternetShop` run:

```bash
docker --context desktop-linux compose -f docker-compose.yaml up -d --build
```

The default compose stack includes:

- Broker 1: `http://localhost:5090`
- Broker 2: `http://localhost:5091`
- Primary database: `localhost:5434`
- Replica database: `localhost:5435`
- Audit, Notification, Inventory, and Analytics subscribers

The non-default ports allow the Warehouse and Internet Shop stacks to run at
the same time.

## Start the API locally

The API image is retained for later use, but the API container belongs to the
optional `api` profile and is not started by the default compose command.

Run the API locally from `InternetShop`:

```bash
dotnet run --project src/InternetShop.csproj
```

The local API uses the Docker databases on ports `5434` and `5435`, and the
brokers on ports `5090` and `5091`.

If the API container is needed later, start it explicitly:

```bash
docker --context desktop-linux compose -f docker-compose.yaml \
  --profile api up -d api
```

## Health and queue state

```bash
curl http://localhost:5000/health
curl http://localhost:5090/health
curl http://localhost:5091/health
curl http://localhost:5090/api/queues
```

The queues and bindings are:

- `internetshop.audit` receives `internetshop.#`
- `internetshop.notifications` receives `internetshop.order.*` and
  `internetshop.payment.completed`
- `internetshop.inventory` receives `internetshop.stock.updated`
- `internetshop.analytics` receives `internetshop.#`

Messages are delivered at least once. A subscriber acknowledges a message only
after handling it successfully. Rejected or expired deliveries are retried up
to three times and then exposed through:

```text
GET /api/queues/{queue}/deadletters
```

## Follow subscriber activity

```bash
docker --context desktop-linux compose -f docker-compose.yaml logs -f \
  audit-subscriber notification-subscriber inventory-subscriber \
  eventanalytics-subscriber
```

## Verify broker failover

```bash
docker --context desktop-linux compose -f docker-compose.yaml \
  stop message-broker

# Continue creating, cancelling, or paying for orders through port 5000.

docker --context desktop-linux compose -f docker-compose.yaml \
  start message-broker
```

Broker state is stored in PostgreSQL, so either broker can publish, receive,
acknowledge, or reject any delivery. Event IDs make publication retries
idempotent.
