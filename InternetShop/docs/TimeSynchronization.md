# Client–server time synchronization

Both console and browser clients maintain an application-level correction to
local time. No administrator permissions or operating system clock changes are
needed. The optional console argument and browser `offset` parameter simulate
clock differences, in seconds, limited to ±86400. Default: zero.

## Server

`GET /api/time` returns a JSON ISO 8601 server timestamp with its local offset.
It does not access the database. Its response is marked `Cache-Control: no-store`.
Every HTTP response passing through the middleware, including errors, carries:

- `X-Server-Received-Time`: UTC timestamp before request processing and lab delays.
- `X-Server-Time`: UTC timestamp immediately before response headers are sent.

Both headers are exposed by CORS to the existing allowed web-client origins.
The middleware runs before `ClientTaskMiddleware`, so time spent waiting for a
lock, semaphore, or artificial delay is included in server processing time.

## Calculation

For each response let:

- t0 = client time immediately before sending;
- t1 = server request-received timestamp;
- t2 = server response-header timestamp;
- t3 = client time when response headers arrive.

```text
correction = ((t1 - t0) + (t2 - t3)) / 2
corrected client time = client local time + correction
request RTT = t3 - t0
server processing time = t2 - t1
estimated one-way network delay = max(0, RTT - server processing time) / 2
```

A +120-second client offset produces a correction near -120 seconds. A
10-second server delay increases request RTT by 10 seconds; it does not increase
the estimated network delay by 5 seconds. Unit tests use a controlled clock to
verify this case with a 100 ms delay in each direction.

The estimate assumes similar travel time in both directions and stable clocks
during the request. It is approximate, not a full NTP implementation. Submillisecond
precision is not available in the browser. Time-zone offsets are handled as
instants. Server time in the panel is the response sample, while corrected client
time is calculated at display time. Panels update when responses arrive.

A valid new sample replaces the saved correction rather than accumulating it.
Missing or invalid timestamps retain the previous correction. The web client
ignores older request samples that finish after a newer sample. No correction is
persisted across client restarts or page reloads. Request send/receive stamps use
raw client time; the web activity log uses corrected time after synchronization.

## Verification

```bash
node --check client/app.js
node tests/client-smoke.cjs
dotnet test tests/InternetShop.Tests.csproj --filter FullyQualifiedName~TimeSynchronizationTests
dotnet test InternetShop.sln
```

The Node checks run without .NET or PostgreSQL and use mocked HTTP. The focused
.NET synchronization tests do not need Docker. The full suite exercises the
race and both protected modes against real PostgreSQL through Docker.
