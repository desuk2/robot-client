# robot-client

Unity 6.3 LTS клиент онлайн-игры про роботов в общем городе, собранном из открытых данных OpenStreetMap (OSM).

## Статус

**Этап «каркас + сетевой срез + визуализация».** Unity 6.3 LTS desktop-проект (без сторонних пакетов и ассетов) с сетевым модулем `NetworkClient`: подключение к robot-srv, `auth`, приём `welcome`/`error`/`snapshot`, отправка `input`. `GameSession` создаёт мир процедурно (плоскость `Plane`, робот-куб `Cube`, направленный свет, ambient, third-person камера, HUD через `OnGUI`), читает WASD/стрелки и шлёт `input` не чаще серверного тика (20 Гц), а робот двигается строго по серверным снапшотам — без предсказания, ассетов и сторонних библиотек.

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — архитектура клиента и план этапов.

## Стек

| Слой | Выбор | Примечание |
|---|---|---|
| Движок | Unity 6.3 LTS (6000.3.x) | desktop (Standalone), Built-in Render Pipeline |
| Язык | C# | без Newtonsoft, System.Text.Json, InputSystem |
| Сериализация | `UnityEngine.JsonUtility` | DTO только public fields, snake_case wire-имена |
| Сеть | `System.Net.WebSockets.ClientWebSocket` | JSON text-фреймы, протокол v1 |
| Рендер | процедурные примитивы | Plane + Cube, без бинарных ассетов |

Пакеты — только встроенные модули Unity (см. `Packages/manifest.json`). Никаких `com.unity.inputsystem`, `com.unity.urp`, `com.unity.cinemachine`, `com.unity.nuget.newtonsoft-json`.

## Ассеты

Игровые ассеты (модели `.glb`/`.fbx`, текстуры, аудио) и OSM-дампы **не хранятся в репозитории**. Они поставляются из внешнего источника (репозиторий ассетов / CDN). Подробности — в политике ассетов (ARCHITECTURE.md) и `.gitignore`. Проект не требует бинарных ассетов для сборки и запуска.

## Запуск

### Сервер

```bash
cd robot-srv
cargo run    # слушает $ROBOT_SRV_ADDR, по умолчанию 127.0.0.1:8080
```

Проверка: `curl http://127.0.0.1:8080/healthz`.

### Клиент (Unity)

1. Установите **Unity Hub** и редактор **Unity 6.3 LTS** (6000.3.x).
2. В Unity Hub: **Add project from disk** → выберите папку `robot-client` (содержит `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`).
3. Unity сам сгенерирует `Assets/...meta`-файлы, если их нет, и соберёт C#-код (`Assets/Scripts`).
4. Откройте сцену `Assets/Scenes/Main.unity` (она уже добавлена в Build Settings) и нажмите **Play**.

Ожидаемый результат: подключение к серверу, отправка `auth`, приём `welcome`
(или понятной `error`), на экране — плоскость мира, оранжевый куб-робот и HUD
(tick, позиция, курс, состояние, скорость).

При первом открытии Unity может предложить обновить `ProjectVersion.txt`/`ProjectSettings.asset`
до актуального патча редактора — подтвердите (миграция безопасна, поля настроек минимальны).

### Управление

| Клавиши | Действие |
|---|---|
| `W` / `↑` | вперёд |
| `S` / `↓` | назад |
| `A` / `←` | влево |
| `D` / `→` | вправо |

Ввод шлётся серверу не чаще `tick_rate` (из `welcome`); позиция робота
обновляется строго по серверным `snapshot` (сервер авторитетен, prediction
в этом срезе нет). `input` до получения `welcome` отбрасывается.

Настройки `GameSession` (инспектор сцены `Main.unity`):

| Параметр | Default | Назначение |
|---|---|---|
| `ServerUrl` | `ws://127.0.0.1:8080/ws` | адрес WebSocket сервера |
| `AuthToken` | (пусто) | заглушка токена, авторизация позже |

## Протокол (контракт robot-srv, байт-в-байт)

Каждое сообщение — конверт v1: `{ "v": 1, "type": "...", "seq": .., "ts": .., "payload": {..} }`.
- `input`: `payload.move.x`, `payload.move.z` (`-1..=1`, диагонали нормализуются сервером).
- `snapshot`: `payload.tick`, `payload.entities[].id/pos{x,y,z}/yaw/state/speed`.
  `yaw` — радианы вокруг Y (`0` = +Z, `π/2` = +X); клиент конвертирует в градусы.
  `pos.y` на проводе всегда `0`; клиент визуально поднимает куб до `y = 0.75`.

## Ограничения

- **Headless**: проект требует графического редактора/плеера Unity. Запуск в
  чистом headless-режиме (`-batchmode -nographics`) не поддерживает ни рендер,
  ни legacy-ввод WASD — вертикальный срез рассчитан на локальный desktop-Play.
- Unity C# компилируется самим редактором; в репозитории нет `.csproj`/`.sln`
  (они генерируются Unity и игнорируются в git).
- `JsonUtility` не умеет полиморфизм: `Envelope` парсит только заголовок,
  `payload` извлекается подстрокой и парсится в типизированный DTO.
- Только text JSON-фреймы: binary-фреймы от сервера (в этом срезе сервер их не
  шлёт) клиент игнорирует. Web-экспорт (WebGL) не входит в этот срез.

## Manual acceptance checklist

1. `cd robot-srv && cargo test` — все тесты зелёные (проверка контракта).
2. `cargo run` в `robot-srv`; `curl http://127.0.0.1:8080/healthz` → `{"status":"ok",...}`.
3. Открыть `robot-client` в Unity 6.3 LTS, сцена `Assets/Scenes/Main.unity`, **Play**.
4. В консоли Unity: `[GameSession] connected`, `[GameSession] welcome: ... tick=20Hz`.
5. HUD: `session=... v1 tick=20Hz`; после первого снапшота — `pos=(0.0, 0.0)`.
6. Нажать и удерживать `W`: HUD показывает `state=moving`, `speed≈5.0`, `pos.z` растёт, куб движется.
7. Отпустить клавиши: `state=idle`, `speed=0.0`, позиция стабильна.
8. Нажать `D`: куб разворачивается (yaw растёт к 90°) и движется по `+X`.
9. Остановить сервер: HUD показывает `disconnected` / `connection failed`, Unity не падает.
10. `git diff --check` и поиск Godot-ссылок в `Assets/`, `Packages/`, `ProjectSettings/` — пусто (см. раздел ниже).

## Проверки без Unity Editor

```bash
cd robot-client
git diff --check                       # нет конфликтов пробелов/EOF
git status                             # только ожидаемые изменения
grep -Ril "godot" Assets Packages ProjectSettings || true
                                       # не должно быть совпадений в коде/конфигах
grep -Ril "Newtonsoft\|System.Text.Json\|InputSystem\|URP\|Cinemachine" Assets Packages ProjectSettings || true
```