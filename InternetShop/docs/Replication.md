# Репликация

## 1. Запустить конетейнеры

```bash
docker compose up -d
```

## 2. Запустить сервер

Из отдельного терминала

```bash
dotnet run --project src/Warehouse.csproj
```

## 3. Запустить клиента

Из отдельного терминала

```bash
dotnet run --project ../ConsoleClient/ConsoleClient.csproj -- 1 http://localhost:5000 0
```

## 4. Посмотреть primary и replica базы

Открыть соответствующие подключения в TablePlus

## 5. Добавить товар

На клиенте

```text
POST /api/lab/prepare-race
```

Отправить запрос, посмотреть в обеих БД наличие вновь добавленного товара

## 6. Положить primary базу

Либо через интерфейс докера, либо через эту команду:

```bash
docker compose stop internet-shop-db
```

Попытаться открыть оба подключения к БД(откроется только replica)

## 7. Добавить данные и проверить, что они добавляются в реплику

От клиента отправить(можно несколько раз)

```text
POST /api/lab/prepare-race
```

Посмотреть изменения в БД

## 8. Поднять primary

Либо через интерфейс докера, либо через команду:

```bash
docker compose start internet-shop-db
```

Посмотреть, что все изменения применились и для primary