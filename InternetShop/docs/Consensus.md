# Демонстрация выбора лидера и консенсуса

В проекте запускается кластер из пяти одинаковых узлов
`InternetShop.ConsensusNode`. Каждый узел отправляет heartbeat-запросы остальным
узлам. Изначально лидером считается доступный узел с наибольшим идентификатором.
Если лидер недоступен, выбирается доступный узел с наибольшим идентификатором.
Восстановившийся узел присоединяется как ведомый и не смещает действующего
лидера, даже если его идентификатор больше.

Для принятия новой версии конфигурации требуется большинство голосов:
`N / 2 + 1`. В кластере из пяти узлов это три голоса.

API интернет-магазина работает как шлюз: получает состояние всех узлов,
определяет текущего лидера и перенаправляет ему предложение. Узлы доступны на
портах `5201`–`5205`, API — на порту `5000`.

## 1. Подготовка

Запустите Docker Desktop и убедитесь, что контекст `desktop-linux` доступен:

```bash
docker context ls
docker --context desktop-linux info
```

Перейдите в каталог проекта:

```bash
cd /home/manmad/Desktop/Internet_shop/InternetShop
```

## 2. Запуск системы

API запускается вместе с остальными сервисами обычной командой Compose:

```bash
docker --context desktop-linux compose up --build -d
```

Проверьте состояние контейнеров:

```bash
docker --context desktop-linux compose ps
```

Должны быть запущены контейнеры `internet-shop-consensus-node-1` —
`internet-shop-consensus-node-5` и `internet-shop-api`. Swagger доступен по
адресу `http://localhost:5000/swagger`.

Если нужно наблюдать за работой heartbeat и сменой лидера:

```bash
docker --context desktop-linux compose logs -f \
  consensus-node-4 consensus-node-5
```

Остановить просмотр логов можно сочетанием `Ctrl+C`; контейнеры продолжат
работать.

## 3. Начальный лидер

Получите состояние кластера через API:

```bash
curl http://localhost:5000/api/consensus
```

В ответе должны быть следующие значения:

```json
{
  "totalNodes": 5,
  "availableNodes": 5,
  "requiredMajority": 3,
  "consensusPossible": true,
  "leaderId": 5
}
```

В полном ответе также находится массив `nodes`. Узел 5 должен иметь роль
`LEADER`, остальные узлы — `FOLLOWER`.

Состояние отдельного узла можно получить напрямую:

```bash
curl http://localhost:5205/state
```

## 4. Успешное достижение консенсуса

Предложите новую версию конфигурации:

```bash
curl -X POST http://localhost:5000/api/consensus/proposals \
  -H 'Content-Type: application/json' \
  -d '{"configurationVersion":15}'
```

При пяти доступных узлах ожидается результат:

```json
{
  "configurationVersion": 15,
  "leaderId": 5,
  "positiveVotes": 5,
  "requiredVotes": 3,
  "totalNodes": 5,
  "achieved": true,
  "message": "CONSENSUS ACHIEVED"
}
```

После этого поле `configurationVersion` в состоянии доступных узлов будет
равно `15`.

## 5. Программная симуляция отказа лидера

Переведите узел 5 в состояние `FAILED`:

```bash
curl -X PUT http://localhost:5000/api/consensus/nodes/5/state \
  -H 'Content-Type: application/json' \
  -d '{"available":false,"acceptVotes":true}'
```

Через одну-две секунды снова запросите состояние:

```bash
curl http://localhost:5000/api/consensus
```

Теперь ожидаются `availableNodes: 4` и `leaderId: 4`. Узел с наибольшим
доступным ID автоматически стал новым лидером.

Восстановите узел 5:

```bash
curl -X PUT http://localhost:5000/api/consensus/nodes/5/state \
  -H 'Content-Type: application/json' \
  -d '{"available":true,"acceptVotes":true}'
```

После следующего heartbeat узел 5 станет ведомым, а узел 4 останется лидером.

## 6. Реальный отказ контейнера лидера

Остановите контейнер пятого узла:

```bash
docker --context desktop-linux compose stop consensus-node-5
```

Подождите две секунды и проверьте кластер:

```bash
curl http://localhost:5000/api/consensus
```

Узел 5 должен отображаться как `FAILED`, а лидером должен стать узел 4.

Запустите контейнер снова:

```bash
docker --context desktop-linux compose start consensus-node-5
```

Через одну-две секунды проверьте, что `leaderId` по-прежнему равен `4`:

```bash
curl http://localhost:5000/api/consensus
```

## 7. Отказ в голосовании без отключения узлов

Настройте узлы 3, 4 и 5 так, чтобы они оставались доступными, но голосовали
`NO`:

```bash
for node in 3 4 5; do
  curl -X PUT "http://localhost:5000/api/consensus/nodes/$node/state" \
    -H 'Content-Type: application/json' \
    -d '{"available":true,"acceptVotes":false}'
done
```

Отправьте предложение с версией больше предыдущей:

```bash
curl -X POST http://localhost:5000/api/consensus/proposals \
  -H 'Content-Type: application/json' \
  -d '{"configurationVersion":16}'
```

Все пять узлов отвечают, но положительных голосов только два. Ожидаются
`positiveVotes: 2`, `requiredVotes: 3`, `achieved: false` и сообщение
`CONSENSUS NOT ACHIEVED`.

Верните нормальное голосование:

```bash
for node in 3 4 5; do
  curl -X PUT "http://localhost:5000/api/consensus/nodes/$node/state" \
    -H 'Content-Type: application/json' \
    -d '{"available":true,"acceptVotes":true}'
done
```

## 8. Потеря большинства узлов

Сымитируйте отказ узлов 3, 4 и 5:

```bash
for node in 3 4 5; do
  curl -X PUT "http://localhost:5000/api/consensus/nodes/$node/state" \
    -H 'Content-Type: application/json' \
    -d '{"available":false,"acceptVotes":true}'
done
```

Проверьте статус:

```bash
curl http://localhost:5000/api/consensus
```

Ожидаются `availableNodes: 2`, `consensusPossible: false` и `leaderId: 2`.
Новое предложение не сможет получить необходимые три голоса:

```bash
curl -X POST http://localhost:5000/api/consensus/proposals \
  -H 'Content-Type: application/json' \
  -d '{"configurationVersion":17}'
```

Восстановите кластер:

```bash
for node in 3 4 5; do
  curl -X PUT "http://localhost:5000/api/consensus/nodes/$node/state" \
    -H 'Content-Type: application/json' \
    -d '{"available":true,"acceptVotes":true}'
done
```

## 9. Остановка

Остановить контейнеры, сохранив их для последующего запуска:

```bash
docker --context desktop-linux compose stop
```

Остановить и удалить контейнеры и сеть проекта:

```bash
docker --context desktop-linux compose down
```

Именованные тома PostgreSQL этой командой не удаляются. Для демонстрации не
используйте флаг `-v`, если данные магазина нужно сохранить.

## Возможные проблемы

### API или узлы не запускаются

Проверьте состояние и логи:

```bash
docker --context desktop-linux compose ps -a
docker --context desktop-linux compose logs api
docker --context desktop-linux compose logs consensus-node-5
```

### Порт уже занят

Для демонстрации должны быть свободны порты `5000`, `5201`–`5205`, `5434`,
`5435`, `5090` и `5091`. Проект `Warehouse` использует для consensus-узлов
порты `5101`–`5105`, поэтому два кластера не конфликтуют между собой.

### Docker не может скачать NuGet-пакеты

Если сборка останавливается с ошибкой `NU1301` для `api.nuget.org`, проверьте
доступ Docker Desktop к интернету и настройки proxy/DNS. После восстановления
доступа повторите команду запуска с `--build`.
