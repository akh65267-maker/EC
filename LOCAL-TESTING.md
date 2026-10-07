# Local testing checklist

Run the whole e-commerce flow on your machine with no Azure needed. Tick each box as you go.

## 0. Prerequisites

- [ ] Docker running (Docker Desktop, or Docker Engine in WSL/Ubuntu)
- [ ] .NET 10 SDK (only for Visual Studio / `dotnet test`)
- [ ] At least **10–15 GB free on C:**. Low disk space makes startup time out with *"likely due to a transient failure"*.

## 1. Start the stack

Pick **one** option.

**A. Everything in Docker** (from the repo root; in WSL: `cd /mnt/d/EC`)
- [ ] `docker compose up -d --build`
- [ ] `docker compose ps` shows 8 containers running (`servicebus-ready` shows *Exited (0)*, which is expected)

**B. Services in Visual Studio (F5), dependencies in Docker**
- [ ] `docker compose up -d postgres redis azurite-net servicebus sqlserver`
- [ ] Set Catalog.API, Basket.API and Order.API as startup projects, then press F5

| Service | Docker (A) | Visual Studio (B) |
|---|---|---|
| Catalog | http://localhost:5001/swagger | http://localhost:5263/swagger |
| Basket | http://localhost:5002/swagger | http://localhost:5183/swagger |
| Order | http://localhost:5003/swagger | http://localhost:5235/swagger |

> Don't run A and B at the same time unless you mean to: both use the same databases.

## 2. Sign in (dev auth bypass)

No Entra ID is needed locally. In Swagger UI click **Authorize** and fill in:

| Field | Value |
|---|---|
| `DevUser` | any name, e.g. `admin`, `alice`, `bob` |
| `DevRoles` | `Catalog.Admin` for admin, or leave empty |

> Swagger pre-fills every `{id}` field with `3fa85f64-...`, which is only an example. Always replace it with a real id, or you'll get **404**.

## 3. Catalog (PostgreSQL + Azurite): as `admin` + `Catalog.Admin`

- [ ] **POST /api/products** → **201**. Copy the `id`.
  ```json
  { "name": "Running Shoe", "description": "Blue", "price": 49.99, "stock": 10 }
  ```
- [ ] **POST /api/products/{id}/image** (real id, any image) → **200** with `imageUrl`
- [ ] Open `imageUrl` in a browser and check the image shows
- [ ] **GET /api/products** includes your product

## 4. Basket (Redis + Service Bus): as `alice`

- [ ] **PUT /api/basket** with your product id → `"total": 99.98`
  ```json
  [ { "productId": "<id>", "productName": "Running Shoe", "price": 49.99, "quantity": 2 } ]
  ```
- [ ] **GET /api/basket** returns the same basket
- [ ] **POST /api/basket/checkout** → **202**. Note the `eventId`.
  ```json
  { "shippingAddress": "Main St 1" }
  ```
- [ ] **GET /api/basket** is now empty

## 5. Order (Service Bus consumer + PostgreSQL): as `alice`

- [ ] **GET /api/orders** shows the order with `id` = `eventId`, `total` 99.98 and `status` *Pending*

## 6. Security rules

- [ ] Catalog, **Logout** (no user), POST product → **401**
- [ ] Catalog as `alice` (no role), POST product → **403**
- [ ] Basket as `bob`, GET basket → empty (not alice's)
- [ ] Order as `bob`, GET `/api/orders/{alice's order id}` → **403**
- [ ] Order as `admin` + `Catalog.Admin`, GET `/api/orders/all` → every user's orders

## 7. Look inside the data stores (optional)

- [ ] PostgreSQL products:
  ```bash
  docker compose exec postgres psql -U postgres -d catalogdb -c 'SELECT "Name","Price","ImageUrl" FROM "Products";'
  ```
- [ ] PostgreSQL orders:
  ```bash
  docker compose exec postgres psql -U postgres -d orderdb -c 'SELECT "UserId","Total","Status" FROM "Orders";'
  ```
- [ ] Redis baskets (before checkout):
  ```bash
  docker compose exec redis redis-cli KEYS 'basket:*'
  ```
- [ ] Azurite images: open http://localhost:10000/devstoreaccount1/product-images?restype=container&comp=list
- [ ] Order consumer log:
  ```bash
  docker compose logs order-api | grep "created for"
  ```

## 8. Automated checks

- [ ] Smoke test, 9 PASS (PowerShell, repo root): `./infra/smoke-test.ps1 -Local`, or add `-VisualStudio` for option B
- [ ] Unit tests: `dotnet test tests/Services.Tests`
- [ ] Integration tests, which need Docker: `dotnet test tests/Services.IntegrationTests`

## Troubleshooting

| Problem | Fix |
|---|---|
| Page not found | Check the URL has no trailing `.` and the port is right; run `docker compose ps` |
| **404** on an `{id}` endpoint | You used Swagger's example id. Paste a real one |
| **401** / **403** | Check the Authorize values (`DevUser`, `DevRoles`) |
| 500 error | `docker compose logs --tail 50 catalog-api` (or `basket-api`, `order-api`) |
| *"likely due to a transient failure"* on startup | Usually low disk space or memory on C:. Free space, then check the **Inner Exception** |
| Order never appears | `docker compose logs order-api`; check the `servicebus` container is up |
| Code changed | `docker compose up -d --build` |
| Start clean | `docker compose down -v` (**deletes local data**), then `docker compose up -d` |
