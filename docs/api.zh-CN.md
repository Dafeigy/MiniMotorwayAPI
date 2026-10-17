# 本地 HTTP API

[English](api.md) | **简体中文**

本文记录当前已实现的接口，以 `Moretoways.cs` 为准。开发计划中的 `/v1/*` 等接口尚未实现，不在本文范围内。

## 通用约定

- 基础地址：`http://localhost:9000`，仅监听本机。响应为 UTF-8 JSON，带 `Cache-Control: no-store`。
- 读取接口使用 `GET`，操作接口使用 `POST`。所有路径接受 `OPTIONS` 预检；CORS 允许任意来源使用 `GET`、`POST` 和 `OPTIONS`，请求头允许 `Content-Type`、`X-Request-Id`。
- 请求在 Unity 主线程上处理。主线程未在 5 秒内开始处理时，排队请求会过期并返回 `503`；已开始的操作可能先返回 `202` 和 `state`，其最终 HTTP 结果可用 `/requests` 查询。
- `/actions/road`、`/actions/clear`、`/actions/traffic-light`、`/actions/roundabout`、`/actions/motorway`、`/actions/upgrade`、`/decision/config`、`/game/new`、`/game/restart` 的请求体限制为 4096（同时检查声明的字节长度和读取后的字符串长度）；JSON 请求使用 `Content-Type: application/json`。`/actions/noop`、`/game/restart`、`/game/pause` 和 `/game/resume` 可发送无请求体的 POST，包括未显式设置 `Content-Length` 的请求。
- 路径区分大小写。未知路径返回 `404`，方法不匹配返回 `405`，未处理的服务器错误返回 `500`。
- 操作请求的响应头包含 `X-Request-Id`。可传入自定义 ID：1–128 个字符，仅限 ASCII 字母、数字、`-`、`_`、`.`、`:`。同一 ID 搭配完全相同的路径及原始请求体会重放首次结果；搭配不同路径或请求体返回 `409`。省略时由服务端生成 ID。比较的是原始请求体，因此仅改变 JSON 空白也视为不同请求。
- 每个操作响应都包含 `requestId`、`actionId` 和提交时的 `episodeId`。`requestId` 跟踪 HTTP／主线程处理，`actionId` 跟踪游戏动作；两者不是同一个标识。
- `202` 表示请求已接受或游戏编辑已排程，**不保证模拟已执行**。用 `/actions?id=<actionId>` 查询最终状态，并继续用后续 `/snapshot` 校验道路、设施、高速公路和资源等可见效果。周升级选择应检查后续 `/game/upgrades`；新游戏或重开应轮询 `/game/status`。

读取 `/snapshot`、`/tiles`、`/tiles/count`、`/destinations` 和 `/game/status` 的成功响应还带 `X-Snapshot-Capture-Ms` 与 `X-Snapshot-Serialize-Ms`，单位为毫秒，分别是主线程捕获和序列化耗时，不含排队及网络时间。

## 接口总览

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/snapshot`、`/` | 完整游戏快照 |
| GET | `/tiles` | 当前快照的全部瓦片 |
| GET | `/tiles/count` | 当前快照的瓦片数量 |
| GET | `/destinations` | 每座目的地一条记录 |
| GET | `/game/status` | 游戏状态与新游戏加载进度 |
| GET | `/game/maps` | 可用于开新局的地图标识与名称 |
| GET | `/game/upgrades` | 当前待选择的周升级候选项及界面可操作状态 |
| GET | `/requests?id=<requestId>` | 查询操作请求的处理状态及原始 HTTP 结果 |
| GET | `/actions?id=<actionId>` | 查询动作的排程与最终执行状态 |
| GET | `/events?after=<eventId>` | 读取有界、轻量的游戏事件日志 |
| GET | `/decision/status` | 读取 Agent／RL 决策策略和当前边界 |
| POST | `/actions/legal` | 预检一项候选动作，不排程模拟命令 |
| POST | `/actions/road` | 添加一条道路连接 |
| POST | `/actions/clear` | 按类型清除指定瓦片上的道路或设施 |
| POST | `/actions/traffic-light` | 在指定瓦片添加交通灯 |
| POST | `/actions/roundabout` | 在指定瓦片添加环岛 |
| POST | `/actions/motorway` | 用两个端点和方向添加高速公路 |
| POST | `/actions/upgrade` | 选择当前周升级候选项 |
| POST | `/actions/noop` | 显式记录无编辑决策，不推进模拟时间 |
| POST | `/decision/config` | 配置 Agent／RL 自动暂停策略 |
| POST | `/game/new` | 开始一局普通模式游戏 |
| POST | `/game/restart` | 重开当前地图 |
| POST | `/game/pause` | 暂停游戏模拟 |
| POST | `/game/resume` | 以正常速度继续游戏 |

## 读取接口

### `GET /snapshot`（`GET /` 为别名）

返回每次请求单独捕获的完整快照。`success` 为 `true`，`timestamp` 为 UTC 时间；`snapshotId` 在每局新游戏开始时重置，此后每次捕获快照时递增。它不是跨多个请求共用的帧编号。`episodeId` 在原生城市初始化边界生成，在本局内保持不变。`simulationTick` 是原生模拟时钟的帧计数，`simulationTime` 是同一时钟的游戏内时间；暂停期间它们不因 HTTP 请求而推进。主要顶层字段如下：

| 字段 | 含义 |
| --- | --- |
| `snapshotId`, `episodeId`, `timestamp` | 快照序号、本局 ID、捕获时间 |
| `simulationTick`, `simulationTime`, `editCommandsExecuted` | 原生模拟帧、模拟时间、已执行的编辑命令累计数 |
| `terminalReason`, `terminalDestinationBuildingId` | 终局原因及导致终局的目的地；非终局时为 `null`。目前普通失败为 `overcrowding`，无法关联目的地的结束为 `gameEnded` |
| `phase` | `menu`、`playing` 或 `gameOver` |
| `gameUI`, `tilemapView`, `camera` | 对应游戏对象是否就绪 |
| `timeScaleMode` | 游戏时间模式名称；无游戏界面时为 `null` |
| `initialSeed`, `map` | 本局实际使用的 seed 和地图 `cityName`；尚未就绪时可能为 `null` |
| `mapBounds` | 地图完整范围 `full` 与随游戏展开的当前范围 `current`，均含 `minX/minY/maxX/maxY` |
| `score`, `clock`, `resources` | 分数、时钟和可用升级资源；不可用时可能为 `null` |
| `tileCount`, `tiles`, `vehicleCount`, `vehicles`, `motorwayCount`, `motorways` | 数量和对应数组 |

`clock` 包含 `time`、`hour`、`day`、`week`。`resources` 是升级类型名称到可用数量的对象，键由当前游戏版本的 `UpgradeType` 决定。无可用 tilemap 时，`tileCount` 可为 `0`；客户端应等待 `phase: "playing"` 且瓦片数量大于零。

每个 `tiles[]` 元素包含：

| 字段 | 含义 |
| --- | --- |
| `coordinates` | `{ "x": 整数, "y": 整数 }` |
| `contentType` | 游戏的瓦片内容类型名称，例如 `None`、`House`、`Destination` |
| `buildingId`, `contentData` | 建筑 ID 和按类型变化的数据；非建筑瓦片可为 `null` |
| `roads` | 方向名称到道路状态名称的对象；仅包含非 `None` 道路 |
| `hasTrafficLight`, `isRoundabout` | 是否有交通灯、是否为环岛中心 |
| `drivewayDirection`, `unbuiltMotorwayId` | 车道入口方向、未建成高速公路 ID |
| `terrain` | `buildable`、`zoneable`、`driveable`、`water`、`mountain`、`currentlyPlayable` |
| `traffic` | 有道路块时为 `laneCount`、当前 `vehicleCount`、`stoppedVehicleCount`、`averageSpeed`；无道路块时为 `null` |

房屋的 `buildingId` 为 `H:x:y`；`contentData` 包含 `groupIndex`、`hasWaitingVehicle`，以及 `vehicleCounts`（`owned`、`waiting`、`available`、`realigning`）。目的地的 `buildingId` 为 `D:minX:minY`，多瓦片建筑的所有瓦片共用该 ID；其 `contentData` 包含：

- `groupIndex`、`totalDemand`、`isOvercrowding`、`isUpgraded`。
- `demand`：`total`、`unassigned`、`assigned`、`unassignedByGroup`、`assignedByGroup`、`maximumBeforeOvercrowding`、`isOvercrowding`、`overcrowdingTime`、`overcrowdingSpeed`、`generationTimer`、`totalServiced`。按组统计对象的键是组编号的 JSON 字符串；计时字段在游戏对象未就绪时可能为 `null`。`totalDemand` 等于 `demand.total`，即 `unassigned + assigned`。
- `footprint`：建筑占用坐标数组。`carpark` 可为 `null`，存在时包含 `topLeft`、`drivewayTopLeft`、`drivewayBottomRight`；不存在的入口坐标为 `null`。

每个 `vehicles[]` 元素包含 `vehicleId`、`state`、`houseBuildingId`、`destinationBuildingId`、`lastVisitedDestinationBuildingId`；三个建筑引用可能为 `null`。需求由目的地产生，未分配需求尚未绑定房屋；派车后才有具体的房屋到目的地关系。`groupIndex` 用于匹配房屋和目的地。

每个 `motorways[]` 元素包含 `motorwayId`、`number`、`state`、`hasConsumedUpgrade`、`startCoordinates`、`startDirection`、`endCoordinates`、`endDirection`。`hasConsumedUpgrade` 取自模拟模型，表示该高速公路是否已占用升级资源。数组按 ID 排序；尚未执行的客户端编辑不会出现在数组中。`motorwayCount` 为数组长度。

### `GET /tiles`、`GET /tiles/count`、`GET /destinations`

- `/tiles`：`{ success, snapshotId, episodeId, simulationTick, simulationTime, count, tiles, timestamp }`；`tiles` 元素与完整快照相同。
- `/tiles/count`：`{ success, snapshotId, episodeId, simulationTick, simulationTime, count, timestamp }`。
- `/destinations`：`{ success, snapshotId, episodeId, simulationTick, simulationTime, count, destinations, timestamp }`；`destinations` 元素仍是瓦片对象，但每个 `buildingId` 仅出现一次。

### `GET /game/status`

返回 `{ success, snapshotId, episodeId, simulationTick, simulationTime, terminalReason, terminalDestinationBuildingId, editCommandsExecuted, phase, gameUI, tilemapView, camera, timeScaleMode, initialSeed, map, tileCount, score, clock, resources, newGame, timestamp }`。`newGame` 为 `null` 或 `{ map, seed, state, error }`；`state` 可为 `loading`、`starting`、`initializing`、`ready`、`failed`。新局加载超过 30 秒会失败并提供 `error`。

### `GET /game/maps`

成功时返回 `{ "success": true, "maps": [{ "map": "LosAngeles", "name": "..." }] }`。`map` 是大小写敏感的 `cityName`，应以运行中的游戏返回值为准。地图服务不可用时返回 `503`。

### `GET /game/upgrades`

返回 `{ success, pending, pendingCount, ready, options }`，不扫描瓦片或构建完整快照。`options` 是当前第一组待选升级，每项为 `{ optionIndex, type, amount, additionalConcrete, selectable }`。`optionIndex` 是候选项原始索引，供 `/actions/upgrade` 使用；`selectable` 表示它当前映射到可点击按钮。`ready` 还要求升级界面已出现且完成过渡，可提交选择。菜单或无候选项时返回空数组及 `pending: false`。

### `GET /requests?id=<requestId>`

成功时返回 `{ success, requestId, actionId, actionState, state, responseStatusCode, response }`。`state` 为 `queued`、`running`、`complete` 或 `expired`；未完成时后两个字段为 `null`，完成后是原始 HTTP 状态码和 JSON 响应。`actionState` 是对应动作的当前状态。缺少或无效 ID 返回 `400`，未知 ID 返回 `404`。

### `GET /actions?id=<actionId>`

返回动作记录：`{ success, actionId, requestId, episodeId, resultingEpisodeId, path, state, resultCode, error, responseStatusCode, queuedAt, scheduledAt, executedAt, scheduledTick, scheduledSimulationTime, executedTick, executedSimulationTime, scheduledCommands, executedCommands, failedCommands }`。`state` 为：

- `queued`：HTTP 请求尚未在游戏主线程开始。
- `scheduled`：至少一个模拟编辑已排程，或新局／重开正在等待新的城市初始化。
- `executed`：所有关联的 `EditTileCommand` 已返回，或同步动作的游戏处理函数已接受操作。新局和重开的 `resultingEpisodeId` 指向新局。
- `rejected`：参数、游戏规则、资源或当前界面拒绝动作。
- `expired`：动作在主线程开始前超时，或所属局已结束而不再允许执行。

道路、清除、交通灯、环岛和高速公路动作会统计 `scheduledCommands` 与 `executedCommands`；高速公路成功时两者应为 `2`。执行时间来自命令实际执行时的原生模拟时钟。暂停／继续和 no-op 是同步 UI 动作，在处理函数返回时记为执行；其可见状态仍应由后续快照确认。服务端保留最近 4096 条请求及动作记录。缺少或无效 ID 返回 `400`，未知或已淘汰 ID 返回 `404`。

### `GET /events?after=<eventId>`

这是轻量读取路径，不扫描 tile、车辆或构建完整快照，也不返回快照计时响应头。`after` 可省略，默认 `0`。返回 `{ success, episodeId, after, oldestEventId, latestEventId, gap, events }`；顶层 `episodeId` 是当前局，每个事件为 `{ eventId, episodeId, simulationTick, simulationTime, timestamp, reasons, details }`。`eventId` 在插件进程内单调递增，不因重开而归零，因此有界缓冲区可能同时包含旧局事件；客户端应按事件自己的 `episodeId` 区分局。同一局、同一原生模拟 tick 内的多个原因合并在一条记录中。当前原因包括：

- `houseAdded`、`destinationAdded`：房屋或目的地模型完成初始化。
- `upgradeAvailable`：新增待选周升级。
- `overcrowdingWarning`：游戏显示即将失败预警。
- `overcrowdingStarted`：目的地拥挤计时速度由非正数变为正数。
- `terminal`：游戏进入终局，详情含原因和目的地。
- `interval`：RL 最小游戏内时间间隔已到达。

日志保留最近 1024 个模拟边界。若 `after` 早于仍保留的第一条记录之前，`gap` 为 `true`，客户端应重新读取完整快照并从 `latestEventId` 继续。事件钩子的精确提前量仍需按游戏版本做运行验证。

### `GET /decision/status`

轻量返回 `{ success, episodeId, mode, minimumSimulationInterval, autoPauseReasons, pending, eventId, simulationTick, simulationTime, reasons, snapshotId }`。`mode` 为 `off`、`agent` 或 `rl`。当决策边界触发时，游戏在本次模拟更新结束后暂停，`pending` 为 `true`；同一边界重复读取 `/snapshot` 会复用 `snapshotId`、模拟 tick 和不可变 DTO，直到动作使缓存失效或游戏恢复。

### `POST /actions/legal`

只读预检一项候选动作，不创建 `requestId`／`actionId`，不排程模拟命令，也不消耗资源。返回 `{ success, type, legal, resultCode, errorPosition }`，候选不合法仍返回 HTTP `200` 并以 `legal: false` 表示；请求格式错误返回 `400`，无活动游戏服务返回 `409`。

请求的 `type` 可为 `road`、`clear`、`traffic-light`、`roundabout`、`motorway`、`upgrade` 或 `noop`。其余字段与对应执行接口相同；`clear` 在此使用 `clearType`，以免与动作类型字段冲突。预检复用游戏原生 `TileEditor` 规则并立即释放生成的临时 edit，因此执行时仍须再次接受资源和地图状态校验；它不是对未来状态的预留。

## 操作接口

以下响应状态是接口本身的处理结果；已排程操作仍需通过读取接口确认游戏状态。

### `POST /actions/road`

请求体示例：`{"x":5,"y":7,"direction":"East"}`。`x`、`y` 为整数。方向可为 `North`、`NorthEast`、`East`、`SouthEast`、`South`、`SouthWest`、`West`、`NorthWest`；解析时不区分大小写。

### `POST /actions/clear`

请求体示例：`{"x":5,"y":7}`。默认清除该瓦片道路；可选 `type` 为 `Roads`、`TrafficLight`、`Roundabout`、`BuiltMotorways`、`UnbuiltMotorway`、`Passages` 或 `Any`，按游戏的永久性规则清除指定类型。`BuiltMotorways` 可在高速公路端点瓦片使用。无效类型返回 `400`。

这两个道路接口共用以下结果：格式错误或缺少字段返回 `400`；无活动游戏、排程被拒绝返回 `409`；编辑服务不可用返回 `503`；游戏规则拒绝编辑返回 `422`，响应含 `resultCode` 和 `errorPosition: {x,y}`。成功但无需修改时返回 `200`，含 `success: true`、`scheduled: false`、`resultCode: "NoChange"`；排程成功返回 `202`，含 `success: true`、`scheduled: true`、`resultCode: "Scheduled"`。

### `POST /actions/traffic-light`、`POST /actions/roundabout`

请求体均为 `{"x":5,"y":7}`。分别调用游戏原生的 `TileEditor.AddTrafficLight` 和 `TileEditor.AddRoundabout`，再沿用道路编辑的客户端视图、模拟命令排程和结果格式。参数错误返回 `400`，游戏规则拒绝返回 `422`，无活动游戏或排程被拒绝返回 `409`，服务不可用返回 `503`。`202` 后读取 `/snapshot` 的 `hasTrafficLight` 或 `isRoundabout` 及资源数量确认最终效果。

### `POST /actions/motorway`

请求体示例：`{"startX":1,"startY":2,"startDirection":"East","endX":8,"endY":2,"endDirection":"West"}`。方向与 `/actions/road` 相同，是两端连接道路的方向。端点不能相同；接口从游戏取得下一个高速公路 ID 和可用编号，先预检完整高速公路，再沿游戏原生流程依次排程未建成端点和完整高速公路编辑。前一编辑占用高速公路资源，后一编辑确定两端位置；长度、地形和端点仍由游戏规则验证。错误状态与其他瓦片编辑相同。`202` 表示两项编辑均已排程，随后读取 `/snapshot.motorways`、端点和资源确认实际效果；一次请求仅添加一条，不包含移动已有高速公路端点。

### `POST /actions/upgrade`

请求体为 `{"optionIndex":0}`，索引必须来自当前 `/game/upgrades.options`。接口只在第一组周升级选择界面已就绪时调用游戏自身的 `GameUpgradeScreen.OnUpgradeSelect`。缺少或无效索引返回 `400`，当前无待选项或界面未就绪返回 `409`，选项不在可点击按钮映射中返回 `422`。界面接受选择后返回 `202`，含 `scheduled: true`、`resultCode: "Scheduled"` 和选中包的信息；游戏选择命令仍需在后续模拟更新执行，应复查待选项和资源。

### `POST /actions/noop`

无需请求体。有活动游戏或升级界面时返回 `200`：`{ "success": true, "scheduled": false, "resultCode": "NoOp" }`；否则返回 `409`。它不编辑地图、不选择升级、不暂停或恢复游戏，也不主动推进模拟时间。决策控制器可在记录无动作决策后调用 `/game/resume`；原生模拟仍可能在 HTTP 请求之间自行推进。

### `POST /decision/config`

请求体可包含 `{ "mode":"rl", "minimumSimulationInterval":30.0, "autoPauseReasons":["upgradeAvailable","overcrowdingWarning","terminal"] }`；省略的字段保留当前值。`minimumSimulationInterval` 必须为正数，单位是游戏原生模拟时间而非现实秒。`autoPauseReasons` 仅接受当前事件原因（不含由 RL 间隔自身产生的 `interval`）。

- `off`：不自动建立决策边界；事件日志仍继续记录。
- `agent`：Agent 轮询 `/events`；只有列入 `autoPauseReasons` 的事件自动暂停。
- `rl`：达到最小模拟时间间隔时自动暂停；列入 `autoPauseReasons` 的关键事件可提前打断间隔。

间隔从本局开始或最近一个 `/actions/*` 动作实际执行的模拟时间计算。边界暂停后，提交具体动作或 `/actions/noop`，再调用 `/game/resume`。如果没有提交动作就恢复，已到期的 RL 间隔会再次立即建立边界。`/game/resume` 清除当前 `pending`，但不会伪造决策动作。配置本身也有 `requestId` 和 `actionId`。

### `POST /game/new`

请求体示例：`{"map":"LosAngeles","seed":12345,"startPaused":true}`。`map` 必填，值来自 `/game/maps`；`seed` 可选，为 `0` 至 `4294967295` 的整数，省略时使用 `20261014`；`startPaused` 可选，默认 `false`。仅开始普通模式游戏，会丢弃当前一局。可从主菜单、地图选择界面或活动游戏界面调用；游戏结束界面需先退出。

接受加载时返回 `202`，响应含 `success: true`、`resultCode: "NewGameLoading"`、`map`、`seed`、`startPaused`。无效 JSON、字段类型或缺少地图返回 `400`；未知地图返回 `404`；界面不支持或已有新局/重开在进行返回 `409`；菜单或地图加载服务不可用返回 `503`。轮询 `/game/status`，直到 `newGame.state` 为 `ready`，且 `phase` 为 `playing`、`map` 和 `initialSeed` 匹配、`tileCount > 0`；`failed` 时检查 `newGame.error`。

### `POST /game/restart`

请求体可省略，或传 `{"seed":12345}`；`seed` 范围同 `/game/new`。省略时复用当前一局的 `initialSeed`。支持活动游戏和游戏结束界面，会丢弃当前一局；接受后返回 `202`，含 `success: true`、`resultCode: "RestartRequested"`、`seed`。无效 JSON 或 seed 返回 `400`；无游戏、seed 不可用或已有启动过程时返回 `409`；重开服务不可用返回 `503`。轮询 `/game/status`，确认进入 `playing`、seed 匹配且 `tileCount > 0`。

### `POST /game/pause`、`POST /game/resume`

无需请求体。有活动游戏时返回 `200`：`{ success, changed, previousMode, currentMode }`；无活动游戏返回 `409`。暂停只停止模拟，不打开暂停菜单；恢复始终选择正常速度。即时响应中的 `changed` 和 `currentMode` 仍可能反映切换前状态，应再读 `/game/status` 或 `/snapshot` 确认。暂停时接受的道路编辑仍可能在后续 Unity 更新中执行。

## 调用示例

```bash
curl http://localhost:9000/game/maps
curl http://localhost:9000/game/status
curl -X POST http://localhost:9000/actions/road \
  -H "Content-Type: application/json" \
  -H "X-Request-Id: road-5-7-east-1" \
  -d '{"x":5,"y":7,"direction":"East"}'
curl 'http://localhost:9000/requests?id=road-5-7-east-1'
curl 'http://localhost:9000/actions?id=<actionId>'
curl 'http://localhost:9000/events?after=0'
```

默认只读的运行验证命令为 `python tests/apitest.py`；道路、暂停和重开等会改变游戏状态的验证需显式传入对应选项。快照字段的后续规划见 [snapshot-roadmap.zh-CN.md](snapshot-roadmap.zh-CN.md)。
