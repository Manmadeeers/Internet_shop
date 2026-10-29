# Manual race-condition test

Run all shell commands from the `InternetShop` directory. Requirements: .NET SDK 8
and Docker with Docker Compose. Use three terminals for the API and two clients.
The console client follows the Warehouse workflow: enter a method and path, then
paste the POST body. Finish every JSON body with an **empty line**. Multiline JSON
and single-line JSON are both accepted. GET requests do not need a body.

## 1. Start the database and build

```bash
docker compose up -d --wait
dotnet build InternetShop.sln
```

On the first start with an empty database volume, Compose applies `schema.sql`
and `seed.sql` automatically. Existing volumes are retained. See `README.md` if
an existing volume has no tables. Warehouse and InternetShop both use port 5432
by default: stop the Warehouse database before starting this one.

## 2. Start the server

```bash
dotnet run --no-build --project src/InternetShop.csproj -- --urls http://localhost:5000 --Lab:RequestDelaySeconds=0 --Lab:RaceDelaySeconds=10
```

The 10-second delay occurs **after checking stock and before writing stock** in
the four demonstration modes. Ordinary `CreateOrder` has no artificial race
delay and uses database product locks.

## 3. Start two independent console clients

In terminal 2:

```bash
dotnet run --no-build --project ConsoleClient/ConsoleClient.csproj -- 1 http://localhost:5000
```

In terminal 3:

```bash
dotnet run --no-build --project ConsoleClient/ConsoleClient.csproj -- 2 http://localhost:5000
```

There are two required arguments: client ID and server URL, plus an optional
clock offset in seconds. The clients send `X-Client-Id`, print HTTP status and
body, and update their saved clock correction from every timestamped response. An empty command exits; Ctrl+C cancels and exits. Connection errors and
HTTP errors leave the client available for another command.

## 4. Prepare one unit of a fresh product

In Client 1:

```text
GET /api/bootstrap
```

Read `customerId`. Then:

```text
POST /api/lab/prepare-race
```

At the JSON prompt, enter:

```json
{}
```

Press Enter on an empty line to send. Read `customerId` and `product.id` from the
response. This creates a new product with `stockQuantity=1` and price 1; existing
products and orders are untouched. Prepare the product **only once per run**.
Both clients must order this same product.

The examples below assume `customerId=1` and `product.id=17`. **Replace both
values with the actual IDs from your response in both clients.**

Inspect the initial shared state:

```text
GET /api/lab/products/17/state
```

Expected: `stockQuantity=1`, `orderedQuantity=0`, `orderIds=[]`.

## 5. Send two overlapping unsafe requests

In Client 1:

```text
POST /api/operations/RaceCreateOrder
```

```json
{
  "customerId": 1,
  "items": [
    { "productId": 7, "quantity": 1 }
  ]
}
```

Finish the JSON with an empty line. Watch the **server console** for Client 1's
`CHECK` with `Available=1`. Before its `WRITE` (within the 10-second delay), send
the **same operation and same JSON** from Client 2. Do not wait for Client 1's
response before starting Client 2.

Wait for **both** responses. Expected: two HTTP 200 responses with two different
order IDs. If Client 2 returns HTTP 400 for insufficient stock, it arrived after
Client 1 wrote; prepare a fresh product and repeat the test with its new ID.

## 6. Read the result

After both responses:

```text
GET /api/lab/products/17/state
```

| Value | Expected result |
| --- | --- |
| Initial stock | 1 |
| Client 1 CHECK | Available=1 |
| Client 2 CHECK | Available=1 |
| Responses | 200 and 200, different order IDs |
| Final `stockQuantity` | 0 |
| Final `orderedQuantity` | 2 |
| Final `orderIds` | Two IDs |

Both request-scoped DbContexts read 1, compute `1 - 1 = 0`, and save the absolute
value 0. The second update overwrites the first, while both orders are saved.
This is a lost update: two units were ordered with only one unit in stock.
The nonnegative-stock database constraint does not detect it, because 0 is valid.
The ReadCommitted transaction preserves each individual order's atomicity; it
does not serialize the read/check/write sequence. `RaceOrderService` deliberately
has no product lock, stock recheck, or concurrency token.

Do not pay or cancel these orders before reading the result. For every repeat,
prepare a **fresh product** and copy its new ID into both request bodies and the
state URL. The IDs in this guide are examples, not fixed seed IDs.

## 7. Compare with resource synchronization

Keep the same server profile. Prepare a **new** product with stock 1 for each
comparison. Use the same operation in both clients and otherwise repeat step 5:

| Operation | Implementation | Expected responses | Ordered quantity |
| --- | --- | --- | --- |
| `RaceCreateOrder` | No gate | 200 + 200 when checks overlap | 2 |
| `TransactionCreateOrder` | PostgreSQL transaction-scoped product locks + lab delay | 200 + 400 | 1 |
| `SyncCreateOrder` | C# `lock` + PostgreSQL product locks | 200 + 400 | 1 |
| `SemaphoreCreateOrder` | `SemaphoreSlim(1, 1)` + PostgreSQL product locks | 200 + 400 | 1 |
| `CreateOrder` | PostgreSQL transaction-scoped product locks | 200 + 400 | 1 |

For lock and semaphore modes, the second request waits before reading product
stock. After the first commits, the second reads 0 and rejects the order.
The final state is `stockQuantity=0`, `orderedQuantity=1`, and one order ID.
Look for `LOCK`, `CHECK`, `WRITE`, and `UNLOCK` in the server log; the semaphore
service also logs `WAIT`.

The C# lock and semaphore serialize their own modes inside one API process.
All protected operations additionally use the same PostgreSQL product-lock keys,
so mixed protected modes and separate API instances also coordinate correctly.
`TransactionCreateOrder` exposes transaction protection with the same lab delay.
`CancelOrder` uses the shared product locks; cancellation and payment share order
locks. Do not mix the intentionally unsafe `RaceCreateOrder` with these operations
on the same product. See [Transactions.md](Transactions.md).

## Optional: browser client

```bash
python3 -m http.server 5500 --directory client
```

Open `http://127.0.0.1:5500/?client=1#lab` and
`http://127.0.0.1:5500/?client=2#lab`. In Client 1, use **Prepare new stock = 1**;
copy the resulting Product ID to Client 2. Select the same **Order mode** in both
tabs, then use **Order 1 unit** during the delay. After both responses, use
**Read shared stock**. Ordinary shopping-cart checkout uses `CreateOrder`.

## Automated checks

```bash
dotnet test tests/InternetShop.Tests.csproj --filter "FullyQualifiedName~Lab2Tests.RaceCondition_OneUnitProducesTwoAcceptedOrders" --logger "console;verbosity=detailed"
dotnet test tests/InternetShop.Tests.csproj --filter "FullyQualifiedName~Lab2Tests.ProtectedOrders_OneUnitAcceptsOnlyOneOrder" --logger "console;verbosity=detailed"
```

The first test waits for both CHECK phases and verifies two saved orders against
one initial unit. The protected-operation theory expects one success and one
rejection, then checks that a failed request released the gate. Cancellation
tests cover all four demonstration modes and verify rollback and subsequent
requests. These tests require Docker and use real PostgreSQL with an in-process
HTTP test server. Expected results above must be confirmed by running the API;
they are not measured results from the archive preparation environment.

## Time synchronization

The lock and semaphore experiments above also update client clock correction.
To make the difference visible, start Client 1 with a +120-second offset and
Client 2 with a -90-second offset:

```bash
dotnet run --no-build --project ConsoleClient/ConsoleClient.csproj -- 1 http://localhost:5000 120
dotnet run --no-build --project ConsoleClient/ConsoleClient.csproj -- 2 http://localhost:5000 -90
```

Run each command in a separate terminal. Enter `GET /api/time` in each client.
Expect corrections close to -120 and +90 seconds respectively. The client prints
raw local time, server response time, saved correction, corrected client time,
send/receive times, total RTT, and estimated one-way network delay. The correction
also updates after HTTP errors that include the server headers.

For the browser, open `http://127.0.0.1:5500/?client=1&offset=120#lab` and
`http://127.0.0.1:5500/?client=2&offset=-90#lab`. Click **Synchronize time** to take
an explicit sample. Every normal response also updates the panel. Clock offsets
only simulate client clock differences; the operating system clock is unchanged.
See [TimeSynchronization.md](TimeSynchronization.md) for the formula and limits.

All automated test groups and their commands are documented in [TESTING.md](TESTING.md).
