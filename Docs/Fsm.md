# FSM 使用说明

平面有限状态机：一份 `FsmGraph` 描述状态，每实体一个 `Fsm` 实例。没有进程门面，不进启动流程，不接 Timer / Input / 事件。调用方自己 `new`、自己决定何时 `Tick` / `Change` / `Stop`。

## 基本用法

```csharp
enum MoveState { Idle, Run }

sealed class IdleState : IState<MoveState, Actor>
{
    public static readonly IdleState Instance = new IdleState();

    public void OnEnter(Fsm<MoveState, Actor> fsm, Actor owner) { }
    public void OnExit(Fsm<MoveState, Actor> fsm, Actor owner) { }
}

sealed class RunState : ITickState<MoveState, Actor>
{
    public static readonly RunState Instance = new RunState();

    public void OnEnter(Fsm<MoveState, Actor> fsm, Actor owner) { }
    public void OnExit(Fsm<MoveState, Actor> fsm, Actor owner) { }

    public void OnTick(Fsm<MoveState, Actor> fsm, Actor owner, float dt)
    {
        if (!owner.HasMoveInput)
        {
            fsm.Change(MoveState.Idle);
        }
    }
}

static readonly FsmGraph<MoveState, Actor> MoveGraph = new FsmGraph<MoveState, Actor>()
    .Add(MoveState.Idle, IdleState.Instance)
    .Add(MoveState.Run, RunState.Instance);

// 每实体
fsm = new Fsm<MoveState, Actor>(MoveGraph, this);
fsm.Start(MoveState.Idle);

void Update()
{
    if (fsm.WantsTick)
    {
        fsm.Tick(Time.deltaTime);
    }
}
```

小图或测试可以用委托 `Add`，不必为每个状态建类型。生产环境状态对象按类型共享（单例），可变数据放在 `TOwner` 上，不要按实体 `new` 一套状态。

## 生命周期

1. 建图、`Add` 注册全部状态。
2. `new Fsm(graph, owner)`，`Start(初始态)` 会 `OnEnter`。任一台机器第一次成功 `Start` 后图冻结，不能再 `Add`。
3. 外部或回调里 `Change(to)`。同 id 为 no-op。
4. 当前态实现了 `ITickState` 时 `WantsTick == true`，由调用方 `Tick`。未实现则 `Tick` 空操作。
5. `Stop` 会 `OnExit` 当前态。未 `Start` 时 `Stop` 为空操作。`Stop` 后再 `Start` 可以。

`Current` 只在 `IsStarted == true` 时有效。未 Start 或已经 Stop 时为 `default(TId)`（enum 通常是 0），不要拿它当业务状态。

## 切换规则

- 回调（`OnEnter` / `OnExit` / `OnTick`）里的 `Change` 不会立刻切，等当前回调返回后再 `OnExit` → `OnEnter`。
- 同一次回调里多次 `Change`，后者覆盖。
- 回调里 `Change` 回当前态会取消已排队的切换（例如 `Change(Run); Change(Idle)` 留在 Idle）。
- 新状态的 `OnTick` 不会在同一次 `Tick` 里跑，下一帧才 Tick。
- 单次泵最多切 16 步，超过视为循环并抛 `FsmException`。此时机器仍是 `IsStarted`，需要 `Stop` 收场。

## 注意事项

**回调里只能排队 `Change`。** `OnEnter` / `OnExit` / `OnTick` 里再调 `Start`、`Tick`、`Stop` 会抛错。死亡、销毁、下树：先让 `Change`/`Tick` 返回，再在外层 `Stop`。

**`Stop` 期间不能 `Change`。** `OnExit` 里还 `Change` 说明停机和切换写在一起了，会抛错；`Stop` 仍会结束（`IsStarted == false`）。嵌套再调 `Stop` 是空操作。

**等待、输入、动画不要写进 FSM 模块。** 前摇用调用方自己的计时，到期再 `Change`；动画事件在回调里 `Change`。移动和战斗要并行就挂两台 `Fsm`，不要做成子状态机。

**图是共享表。** 状态逻辑必须可重入、无实体字段。多实例互不影响靠的是各自的 `Fsm` + `owner`。

**异常会丢掉本次排队的切换。** `OnEnter` 里先 `Change(Run)` 再抛错，不会进 Run；下一次外部 `Change` 也不会把 Run 补上。回调里第二个 `Change` 若目标未注册，同样清掉前面已排队的目标。

**先注册再切。** 未注册 id、重复 `Add`、未 Start 就 `Change`、重复 `Start` 都会抛 `FsmException`。外部 `Change` 会先检查目标存在，再退出当前态。

## 运行时演示

Play 后在 Hierarchy 选中打开的 `TestMainPanel`，Inspector 右键组件 → **Run Fsm Demo**。Console 过滤 `[FsmDemo]`。不会在每次打开面板时自动跑。
