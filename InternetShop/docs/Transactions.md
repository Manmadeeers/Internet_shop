1. docker compose up -d --no-build

2. Сервер

dotnet run

3. Клиент 1(из папки клиента)

dotnet run --project ConsoleClient.csproj -- 1 http://localhost:5000 0

4. Клиент 2(из папки клиента)

dotnet run --project ConsoleClient.csproj -- 2 http://localhost:5000 0

5. Синхронизация

На первом:

POST /api/lab/prepare-race

Снова на первом

POST /api/operations/CreateOrder

```json
{
  "customerId": 1,
  "items": [
    { "productId": 7, "quantity": 1 }
  ]
}
```

На втором то же самое.

Отправить запрос сначала на первом, потом на втором и подождать

На первом должно быть 200, на втором - 400.

6. Ошибка без транзакции

На первом

POST /api/operations/PayOrderFailNT

(Используй заказ, который создался на первом клиенте на предыдущем шаге)

```json
{
  "orderId":5,
  "Amount":1,
  "Method":"Card"
}
```

Должно быть 500.

В TablePlus проверить:

```sql
select * from Payment;
```

должен появиться новый

После этого проверить статус оплачиваемого заказа

```sql
select * from Orders where id=5;
```

Он всё ещё должен быть Pending

7. Ошибка с транзакцией

На первом:

POST /api/lab/prepare-race

POST /api/operations/CreateOrder

```json
{
  "customerId": 1,
  "items": [
    { "productId": 7, "quantity": 1 }
  ]
}
```

POST /api/operations/PayOrderFail

```json
{
  "orderId":5,
  "Amount":1,
  "Method":"Card"
}
```

должно быть 500

После этого проверить в БД:

```sql
select * from Payment
```

Новых появиться не должно, так как произошёл Rollback


8. Успешная транзакция

на первом:

POST /api/operations/PayOrder

(Использовать id заказа из предыдущего шага, потому что он не оплачен из-за роллбэка)

```json
{
  "orderId":5,
  "Amount":1,
  "Method":"Card"
}
```

должно быть 200