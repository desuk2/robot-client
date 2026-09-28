# Архитектура robot-client

Версия: 0.3 (Unity 6.3 LTS, сетевой срез + визуализация)
Дата: 2026-09-28

## 1. Цель

Клиент онлайн-игры на Unity 6.3 LTS: игрок управляет роботом в общем городе на базе OSM. Сервер авторитетен (см. репозиторий robot-srv); клиент отвечает за ввод, визуализацию, сглаживание сетевого потока (prediction/interpolation) и интерфейс.

## 2. Стек

| Слой | Выбор | Обоснование |
|---|---|---|
| Движок | Unity 6.3 LTS (6000.3.x), Built-in RP | desktop (Standalone), без URP/WebGL-пакетов |
| Язык | C# | типизация, производительность критичных путей |
| Сеть | `ClientWebSocket`, протокол v1 | общая спецификация с robot-srv |
| Сериализация | `UnityEngine.JsonUtility` | только встроенные модули, без Newtonsoft/System.Text.Json |
| Ввод | legacy `Input.GetKey` (Input Manager) | без пакета InputSystem; `activeInputHandler: 0` |
| Рендер | процедурные примитивы | Plane/Cube/свет — без бинарных ассетов |

Пакеты ограничены встроенными модулями Unity (`Packages/manifest.json`):
`com.unity.modules.*` версии `1.0.0`. Пакеты `com.unity.inputsystem`,
`com.unity.render-pipelines.universal`, `com.unity.cinemachine`,
`com.unity.nuget.newtonsoft-json` **не используются**.

## 3. Целевая структура репозитория

```
robot-client/
├── Packages/manifest.json        # только встроенные модули Unity
├── ProjectSettings/              # ProjectVersion.txt, ProjectSettings.asset, EditorBuildSettings.asset
├── Assets/
│   ├── Scenes/Main.unity         # Bootstrap-сцена с MonoBehaviour GameSession
│   └── Scripts/
│       ├── Protocol.cs           # DTO v1 (JsonUtility, public fields, snake_case)
│       ├── NetworkClient.cs      # WebSocket: auth/welcome/error/input/snapshot
│       └── Bootstrap/GameSession.cs  # мир, ввод, drain снапшотов, HUD
├── docs/
└── .gitignore                    # Library/Temp/Obj/Logs/UserSettings, авто-*.csproj/*.sln, ассеты, OSM
```

Unity генерирует `Assets/*.meta`, `Library/`, `Temp/`, `*.csproj`/`*.sln` и
`UserSettings/` при первом открытии — эти артефакты игнорируются git и не коммитятся.
Стабильные GUID (`.meta`) заведены только для сцены и скриптов, чтобы рукописная
YAML-сцена `Main.unity` корректно ссылалась на `GameSession`.

## 4. Камера: FPS / third-person

- **Third-person (текущий срез)**: камера следует за роботом с офсетом
  `(0, +7, -9)` и `LookAt` на корпус; сглаживание — `Vector3.Lerp`.
- **FPS** (план): камера привязана к голове робота, захват мыши.
- Переключение режимов не влияет на сеть: серверу всегда уходит одинаковый `input`.

## 5. Сетевой слой

- Транспорт: WebSocket (`ClientWebSocket`); адрес по умолчанию `ws://127.0.0.1:8080/ws`.
- Подключение: `auth` (токен, версия клиента) → `welcome` (session_id, protocol_version, tick_rate).
- Конверт v1 (JSON text-фрейм): `{ "v": 1, "type": "...", "seq": .., "ts": .., "payload": {..} }`.
  Поле-порядок и имена совпадают с serde-структурой на robot-srv (байт-в-байт).
- **Threading**: receive-loop `NetworkClient.RunAsync` исполняется в фоновом
  `Task.Run`. События (`WelcomeReceived`, `ErrorReceived`, `SnapshotReceived`,
  `Disconnected`) приходят с фонового потока; **колбэки не трогают Unity API** —
  они только фиксируют состояние под локом. Main-поток (`GameSession.Update`)
  забирает последний снапшот и применяет его к сцене.
- **No input before welcome**: `NetworkClient` выставляет флаг после приёма
  `welcome`; `SendInputAsync` до этого возвращает `false` и ничего не шлёт.
- **Send lock**: `SemaphoreSlim(1,1)` — `ClientWebSocket` допускает только один
  незавершённый send.
- Keepalive `ping`/`pong` и реконнект с backoff — план (этап 3).
- JsonUtility не поддерживает полиморфизм: заголовок парсится в `Envelope`
  (поле `payload` игнорируется), затем подстрока payload извлекается
  (`Json.ExtractPayload`) и парсится в типизированный DTO.

### Текущий срез

- `NetworkClient` шлёт `auth` и `input` (`{ "move": { "x": .., "z": .. } }`),
  принимает `welcome`, `error`, `snapshot`.
- `GameSession` читает WASD/стрелки и шлёт `input` **не чаще** `tick_rate`
  (из `welcome`); на изменение посылается одно сообщение (сервер хранит
  последний ввод между тиками).
- Робот-куб позиционируется строго по серверным `snapshot`:
  - `pos.x/z` — напрямую; `pos.y` сервера всегда `0`, визуально куб поднят до `0.75`
    (половина высоты 1.5), чтобы «стоять» на плоскости;
  - `yaw` сервера — радианы (`0` = +Z, `π/2` = +X) → `Quaternion.Euler(0, yaw*Rad2Deg, 0)`.
- Мир отрисовывается процедурно (`Plane`-примитив 100×100, `Cube`-робот,
  направленный свет, flat ambient), камера и HUD (`OnGUI`) создаются в коде.

## 6. Prediction / interpolation

Цель — отзывчивое управление и плавное отображение чужих роботов при 20 Гц сервера.

- **Client-side prediction** (свой робот): локальное продвижение по `input` в интервалах между снапшотами; откат (reconciliation) при расхождении больше порога.
- **Interpolation** (чужие роботы): буфер снапшотов 100–150 мс, интерполяция позиции и поворота между двумя последними снапшотами.
- **Extrapolation** (опционально): предсказание движения при потере снапшотов.

## 7. Навигация

- План: маршруты от сервера (граф OSM → `route(a, b)`) транслируются клиентом в путь: ломаная точек + дистанция; перерисовка при `order.updated`.

## 8. Рендер карты

- Данные карты приходят с сервера (`map.meta`, тайлы/геометрия) — клиент не хранит OSM-дампы локально.
- Векторные тайлы → меши/коллайдеры, подгрузка по секциям вокруг игрока.

## 9. Политика ассетов

**В репозитории запрещены любые бинарные игровые ассеты и OSM-дампы:**

- модели: `.glb`, `.fbx`, `.dae`, `.obj`, `.blend`;
- текстуры: `.png`, `.jpg`, `.jpeg`, `.tga`, `.exr`, `.hdr`, `.ktx2`;
- аудио: `.wav`, `.ogg`, `.mp3`;
- карта: `.osm`, `.osm.pbf`, `.pbf`;
- кэш движка: `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`.

Правила:

1. Ассеты живут во внешнем репозитории ассетов / CDN и скачиваются при сборке.
2. Git LFS не используется.
3. В git — только исходники: `.cs`, `.unity` (рукописная минимальная сцена), конфиги `Packages/`, `ProjectSettings/`.
4. Папка `assets/` игнорируется в git.

## 10. План этапов

| Этап | Содержание | Критерий готовности |
|---|---|---|
| **0 — Bootstrap** | архитектура, план | docs приняты |
| **1 — Каркас** (готово) | Unity 6.3 LTS проект, Packages, сцена Main.unity | проект открывается в редакторе, есть сцена |
| **2 — Контроллер** (частично) | движение робота, камеры FPS/third-person, переключение | управление работает; в срезе — ввод + позиция из снапшотов |
| **3 — Сеть** (частично) | WebSocket, auth/welcome, heartbeat, реконнект | подключение к серверу, поток снапшотов; heartbeat/reconnect — план |
| 4 — Pred/Interp | prediction своего робота, интерполяция чужих | двое роботов плавно двигаются |
| 5 — Карта | приём тайлов, рендер дорог, коллизии | карта сервера отображается |
| 6 — UI | HUD заказов, мини-карта, инвентарь, магазин модов | полный цикл заказа из UI |
| 7 — Полировка | анимации, звук, эффекты, offline-режим | сборка-кандидат |
| 8 — Интеграция | совместные тесты с robot-srv, beta | публичная beta |

## 11. Ограничения Unity-среза

- **Headless**: вертикальный срез рассчитан на локальный desktop-Play в редакторе.
  `-batchmode -nographics` не даёт ни рендера, ни legacy-ввода — headless-запуск не поддержан.
- `.csproj`/`.sln` и `Library/` генерируются Unity и не хранятся в git.
- `ProjectSettings.asset` минимальный — Unity мигрирует его до актуальной версии при первом открытии.
- Сборка Unity C# происходит редактором; наличие компилятора вне редактора не требуется.

## 12. Manual acceptance checklist

См. README.md → «Manual acceptance checklist». Коротко:

1. `cargo test` в robot-srv — зелёные.
2. `cargo run` сервера + `curl /healthz`.
3. Открыть проект в Unity 6.3 LTS, Play сцены `Main.unity`.
4. Консоль Unity: `connected` → `welcome ... tick=20Hz`.
5. HUD: снапшоты, `W` → `moving`/`speed≈5`, `pos.z` растёт; отпускание → `idle`.
6. `D` → yaw растёт к 90°, движение по +X.
7. Остановка сервера → HUD `disconnected`, без падения Unity.
8. `git diff --check`, отсутствие Godot-ссылок и бинарных ассетов.

## 13. Глоссарий

- **Snapshot** — серверный слепок состояния на тик.
- **Client-side prediction** — локальный прогноз по собственному вводу.
- **Interpolation buffer** — буфер снапшотов для плавного отображения.
- **Reconciliation** — синхронизация предсказания с серверной истиной.
- **Envelope v1** — конверт `{v,type,seq,ts,payload}`, общий для всех сообщений протокола.