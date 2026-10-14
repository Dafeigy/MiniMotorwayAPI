# `/snapshot` 接口开发路线

本文记录 `/snapshot` 后续扩展范围、游戏实体语义和暂不实现的内容。所有新增字段均应保持向后兼容，不删除或改变现有字段的含义。

## 实体与客流语义

### Destination（目的地）

`DestinationModel` 是产生并持有客流需求（pin）的实体。车辆到达目的地后会消除一项已经分配的需求并增加分数。

目的地拥有或共享 `CarparkModel`，因此可以接收车辆、让车辆短暂停放，并提供车辆返回原房屋所需的出口。但它不是车辆供给方，也不拥有可供其他需求调度的车辆。双目的地可能共享同一个停车场。

### House（房屋）

`HouseModel` 是车辆的拥有者和供给方。房屋与需求组通过 `groupIndex` 匹配；当目的地存在未分配需求时，调度逻辑会从对应组中选择当前有可用车辆、道路可达且路径成本最低的房屋。

因此，一项需求在生成时只有“需求组 → 目的地”的关系，并不预先绑定具体房屋。车辆实际派出后，才形成明确的“房屋 → 目的地”行程；完成送达后，车辆再从目的地返回所属房屋。

## 当前接口状态

当前 `/snapshot` 已包含：

- `initialSeed`，表示本局初始化实际使用的 seed。
- 房屋的 `groupIndex` 和 `hasWaitingVehicle`。
- 目的地的 `groupIndex`、`totalDemand`、`isOvercrowding`、升级状态、占地和停车场入口。

其中：

```text
totalDemand = unassignedDemandCount + assignedDemandCount
```

- `unassignedDemand`：尚未选定车辆的需求。
- `waitingDemand`：已经分配车辆、但车辆尚未完成送达的需求。接口中计划使用更明确的名称 `assignedDemand`。

现有 `totalDemand` 无法区分以上两种状态，也无法表达已经派出的车辆来自哪栋房屋。

## 第一阶段：需求明细（已实现并完成运行验证）

在目的地 `contentData` 中追加需求信息，同时保留现有 `totalDemand` 和 `isOvercrowding`：

```json
{
  "demand": {
    "total": 4,
    "unassigned": 2,
    "assigned": 2,
    "unassignedByGroup": { "0": 2 },
    "assignedByGroup": { "0": 2 },
    "maximumBeforeOvercrowding": 6,
    "isOvercrowding": false,
    "overcrowdingTime": 0.0,
    "overcrowdingSpeed": 0.0,
    "generationTimer": 1.25,
    "totalServiced": 18
  }
}
```

具体字段名以实现时的运行验证结果为准，但至少需要提供：

- 未分配需求数量。
- 已分配但尚未完成的需求数量。
- 两类需求按实际调度组的计数。
- 触发拥堵计时的需求阈值。
- 当前拥堵时间和速度。
- 累计已完成需求数量。

`generationTimer` 属于需求生成节奏，不是拥堵倒计时；实现和文档中必须避免混用。

## 第二阶段：车辆与活跃行程关系（已实现并完成运行验证）

在 `/snapshot` 顶层增加 `vehicles`。第一版只暴露客流关系和行为状态，不计算完整路径：

```json
{
  "vehicles": [
    {
      "vehicleId": 12,
      "state": "DrivingToDestination",
      "houseBuildingId": "H:-5:-1",
      "destinationBuildingId": "D:-12:-3",
      "lastVisitedDestinationBuildingId": null
    }
  ]
}
```

要求：

- `vehicleId` 在车辆生命周期内稳定。
- `houseBuildingId` 始终指向车辆所属房屋。
- `destinationBuildingId` 只在车辆当前已分配目的地时填写。
- 返回途中的车辆通过状态和 `lastVisitedDestinationBuildingId` 表达，不把目的地误写成车辆供给方。
- 客户端可以从 `destinationBuildingId != null` 的车辆推导活跃送达行程；暂不重复维护另一套 `activeTrips` 数据，避免两个列表不一致。

后续如有明确客户端需求，可增加只读的 `/vehicles` 端点，但其内容应与同一次 snapshot 使用相同的数据结构和 `snapshotId`。

## 第三阶段：房屋车辆统计（已实现并完成运行验证）

在房屋 `contentData` 中保留 `hasWaitingVehicle`，并追加：

```json
{
  "vehicleCounts": {
    "owned": 2,
    "waiting": 1,
    "available": 1,
    "realigning": 0
  }
}
```

实现时应统一从同一次主线程 snapshot 中读取，保证房屋统计和顶层 `vehicles` 一致。测试至少验证：

- 各计数非负且不大于 `owned`。
- `hasWaitingVehicle == (waiting > 0)`。
- 每辆车的 `houseBuildingId` 能对应到 snapshot 中的一栋房屋。

## Seed 和局信息完善

`initialSeed` 已经实现。后续为了让它具有完整上下文，计划追加：

- `city`。
- `gameMode`。
- `seedSource`，例如 `fixedNormal` 或 `challenge`。
- `simulationFrame`，用于区分模拟时间；现有 `snapshotId` 仅表示 API 捕获次数。
- 明确菜单阶段的生命周期：没有活动游戏时不应把上一局 seed 表示为当前局 seed。

`initialSeed` 只表示初始化 seed，不代表当前随机数生成器的完整状态。严格复现仍依赖游戏版本、城市、模式、挑战配置以及相同的操作和时序。

## 当前非目标

以下内容暂不进入本轮实现：

- 房屋与目的地之间的完整路径成本矩阵。
- 为每辆车序列化完整车道路径。
- 实时位置、速度和 ETA。
- 未来需求预测或随机数生成器内部状态。

完整路径矩阵需要对大量房屋与目的地组合执行寻路，可能显著增加频繁调用 `/snapshot` 时的主线程负担。在出现明确使用场景和性能预算前，不应默认计算。

## 验收原则

- 所有 Unity 和模拟对象继续只在主线程读取。
- 新字段为追加式变更，现有客户端无需修改即可继续使用。
- `total == unassigned + assigned`。
- 分组计数之和分别等于 `unassigned` 和 `assigned`。
- 已分配需求与指向该目的地的出发车辆数量应在同一模拟帧保持一致。
- 菜单、游玩中和游戏结束三个阶段均返回语义明确的数据或 `null`，不复用上一局状态冒充当前状态。
- 不在 snapshot 捕获过程中主动执行寻路或修改模拟状态。
