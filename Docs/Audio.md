# GameAudio 用法

进程内音频入口是 `GameAudio`。业务只通过它播放、停、调音量；不要直接创建
`AudioSource`，也不要销毁 `[GameAudio]` 节点。

YooAsset location 按文件名寻址。当前资源在 `Assets/Art/Audio`，配置在
`Assets/Audio/Config/DefaultAudioRuntimeConfig.asset`。业务 ID 写在
`GameAudioIds`。

---

## 1. 启动与关闭

### 用法

`Launch` 里的顺序：

```csharp
GameTimer.Init(transform, TimerSchedulerOptions.LargeGameDefault());
var package = await ResourcesLoadManager.Instance.CreatePackageAsync();

await GameAudio.InitAsync(
    package,
    audioConfig,          // Launch 上的 AudioRuntimeConfig
    transform,
    startupCancellation.Token);

UI.Init(package);
GamePool.Init(package, transform);
```

退出：

```csharp
UI.Shutdown();
if (GameAudio.IsInited)
{
    await GameAudio.ShutdownAsync();
}
GameTimer.Shutdown();
GamePool.Shutdown();
```

`InitAsync` 会校验 Mixer、目录和 YooAsset location，并预加载 `Resident` 条目。
`persistRoot` 只要求 Init 时物体仍在 Hierarchy 中；音频节点自己 `DontDestroyOnLoad`，
不要把它当成父节点去挂。

### 注意

- 必须先 Init，再播放。未 Init、重复 Init、未 Shutdown 再 Init 都会抛。
- 只能在 Unity 主线程调用公开 API。
- 退出用 `ShutdownAsync`：它会取消进行中的播放请求，等加载结束后再拆。
- 同步 `Shutdown` 要求此时已经没有异步播放和加载，否则抛。业务退出不要用它。
- 不要 `Destroy` `[GameAudio]`。拆掉之后 `IsInited` 为 false，播放接口会抛，且
  `Launch` 可能跳过关闭。
- `InitAsync` / 播放可传 `CancellationToken`。启动被取消时，Init 会把已创建部分清掉再抛。

---

## 2. 怎么选接口

| 场景 | API | 失败时 |
|------|-----|--------|
| 音效、UI 音 | `TryPlayAsync` | 返回 `AudioPlayResult`，看 `Rejection` |
| 必须播出的音效 | `PlayAsync` | 冷却/声道满等抛 `AudioPlaybackRejectedException` |
| BGM | `PlayBgmAsync` / `TryPlayBgmAsync` | 同上；BGM 不能走 `PlayAsync` |
| 停某一声 | `Stop` / `TryStop` | `Stop` 遇到过期句柄会抛；`TryStop` 返回 false |
| 停当前 BGM | `StopBgm` | 没有在播则返回 0 |
| 停一整条总线 | `StopBus` | — |

BGM 有专用双声道和交叉淡化，后一次请求覆盖前一次。音效挤占声道时，不会抢走 BGM。

---

## 3. 播放音效

```csharp
using Game.Audio;
using Cysharp.Threading.Tasks;

AudioPlayResult result = await GameAudio.TryPlayAsync(
    GameAudioIds.Dice,
    OpenCancellationToken);

if (!result.IsPlaying)
{
    // Cooldown / InstanceLimit / VoiceLimit，按玩法忽略即可
    return;
}

// 需要中途停时才保留句柄
GameAudio.TryStop(result.Handle, fadeOutSeconds: 0.1f);
```

3D、音量、音高：

```csharp
AudioPlayOptions options = AudioPlayOptions
    .At(worldPosition)
    .WithVolume(0.8f)
    .WithPitch(1.1f);

await GameAudio.TryPlayAsync(GameAudioIds.Eat, options, cancellationToken);
```

- 配置里 `spatialBlend > 0` 的条目必须用 `At` / 带世界坐标的 options。
- 2D 条目不能带世界坐标，否则抛。
- 最终音量 = 条目 volume × `VolumeScale`，必须在 0..1；音高乘积必须在 0.01..3。

面板关闭时把 `OpenCancellationToken` 传进去。请求还在加载会被取消；已经开始播的短音效
不会被令牌单独掐掉，需要 `TryStop` 或关面板时 `StopBus`。

---

## 4. 播放 BGM

```csharp
SoundHandle bgm = await GameAudio.PlayBgmAsync(
    GameAudioIds.LudoBgm,
    crossFadeSeconds: 0.5f,
    cancellationToken);

if (GameAudio.IsPlaying(bgm))
{
    GameAudio.Stop(bgm, 0.25f);
}

// 关卡进出不关心句柄时：
GameAudio.StopBgm(0.25f);
bool playing = GameAudio.IsBgmPlaying();
```

切换曲目再调一次 `PlayBgmAsync` 即可，旧曲会按 `crossFadeSeconds` 淡出。

并行切 BGM、且冷却/声道拒绝不算事故时，用 `TryPlayBgmAsync`：

```csharp
AudioPlayResult result = await GameAudio.TryPlayBgmAsync(
    GameAudioIds.LudoBgm,
    0.5f,
    cancellationToken);

if (!result.IsPlaying)
{
    // Superseded：被更新的 BGM 请求取代
    return;
}
```

### 注意

- 配置为 BGM 的条目只能走 BGM 接口；反过来，音效 ID 不能传给 `PlayBgmAsync`。
- BGM 必须是 2D，`maxInstances` 必须为 2（交叉淡化）。
- 关闭面板后仍可能有一次正在加载的 BGM 完成。要用打开代次或 `cancellationToken`
  判断过期，过期后 `TryStop` 刚拿到的句柄，不要写到新一轮 UI 上。
- `IsBgmPlaying()` 在淡出结束前仍为 true。按钮「播放/停止」如果要在淡出期间允许再播，
  应记住自己的 `SoundHandle`，不要用 `IsBgmPlaying` 做开关。

---

## 5. 音量

线性 0..1，内部写成 Mixer 分贝。参数名必须在 Mixer 里暴露。

```csharp
GameAudio.SetMasterVolume(0.8f);
GameAudio.SetBusVolume(AudioBus.Sfx, 0.5f);

float master = GameAudio.GetMasterVolume();
float bgm = GameAudio.GetBusVolume(AudioBus.Bgm);
```

| 总线 | Mixer Group | 音量参数 |
|------|-------------|----------|
| Master | Master | MasterVolume |
| Bgm | Master/BGM | BgmVolume |
| Sfx | Master/SFX | SfxVolume |
| Ui | Master/UI | UiVolume |
| Voice | Master/Voice | VoiceVolume |

`AudioBus.Ui` 忽略 `AudioListener.pause`，暂停菜单里 UI 音仍可播完。SFX / BGM / Voice
在全局暂停期间不算播完，解除暂停后继续。

---

## 6. 配表

在 `AudioRuntimeConfig` 里加条目，并在 `GameAudioIds` 增加同名常量。`id` 必须唯一；
`location` 必须是 YooAsset 里已有的地址（当前为文件名，如 `DiceSound`）。

| 字段 | 含义 |
|------|------|
| bus | Bgm / Sfx / Ui / Voice |
| maxInstances | 同 ID 同时播放上限。非 BGM 不能超过 `maxVoices` |
| cooldownSeconds | 同 ID 两次开播的最小间隔 |
| overflowPolicy | 见下表 |
| priority | Unity 优先级，0 最重要，256 最不重要 |
| volume / pitch / loop | 默认播放参数 |
| spatialBlend | 0 为 2D，大于 0 为 3D，播放时必须给世界坐标 |
| loadMode | 见下表 |

溢出策略：

| 策略 | 行为 |
|------|------|
| Reject | 满了就拒绝，返回 `InstanceLimit` / `VoiceLimit` |
| StopOldest | 停最早的一条再播 |
| StopLowestPriority | 停优先级数字更大（更不重要）的；对方更重要则拒绝 |

加载模式：

| 模式 | 行为 |
|------|------|
| OnDemand | 第一次播放时加载，引用归零后按 `cacheRetentionSeconds`（默认 30s）卸载 |
| Scene | 归属到播放时的 Active Scene，或 `options.InScene(scene)`。该场景卸载后，空闲缓存释放 |
| Resident | Init 时加载，一直持有到 Shutdown |

同一 `location` 不能配两种 `loadMode`。

当前默认目录：

| ID | 资源 | 总线 | 加载 |
|----|------|------|------|
| `bgm.ludo` | LudoBgMusic | Bgm | Resident |
| `sfx.dice` | DiceSound | Sfx | OnDemand |
| `sfx.dice.alt` | Dice1Sound | Sfx | OnDemand |
| `sfx.eat` | EatDice | Sfx | OnDemand |
| `sfx.move.1` | MoveDice1Sound | Sfx | OnDemand |
| `sfx.move.2` | MoveDice2Sound | Sfx | OnDemand |

---

## 7. 场景音频

OnDemand / Resident 不用管场景。只有 `loadMode = Scene` 的条目需要：

```csharp
await GameAudio.TryPlayAsync(
    id,
    AudioPlayOptions.Default.InScene(gameplayScene),
    cancellationToken);

GameAudio.UnloadSceneAudio(gameplayScene); // 只卸这一场景的 Scene 缓存
GameAudio.UnloadSceneAudio();              // 卸全部 Scene 缓存
```

不写 `InScene` 时，记的是调用当下的 Active Scene。Driver 会在 `sceneUnloaded` 时
按 handle 释放对应空闲缓存；仍在播放的声音播完后再放。

Additive 场景请显式 `InScene`，不要依赖 Active Scene。

---

## 8. 句柄与查询

`SoundHandle` 是声道代次。声音自然结束、被抢占或 `Stop` 之后，旧句柄失效。

```csharp
GameAudio.IsPlaying(handle);                 // 该句柄是否仍占着声道（含 Listener 暂停）
GameAudio.GetActiveVoiceCount();             // 全部
GameAudio.GetActiveVoiceCount(AudioBus.Sfx); // 某总线
GameAudio.IsBgmPlaying();
```

过期句柄用 `Stop` 会抛，用 `TryStop` 得到 false。关界面、关关卡用 `TryStop` /
`StopBgm` / `StopBus`。

---

## 9. 拒绝原因

`AudioPlayResult.Rejection`：

| 值 | 含义 |
|----|------|
| None | 已开始播，`Handle` 有效 |
| Cooldown | 同 ID 还在冷却 |
| InstanceLimit | 同 ID 达到 maxInstances，且策略不允许抢 |
| VoiceLimit | 常规声道满，且不能抢更高保护总线（BGM / Voice） |
| Superseded | 仅 BGM：加载完成前已被更新的 BGM 请求取代 |

参数错误、未配置的 ID、未 Init、错总线、2D/3D 不匹配属于编程错误，会抛，不会变成
`Rejection`。

---

## 10. 面板里的推荐写法

```csharp
SoundHandle _bgmHandle;
uint _audioScope;

void OnClickBgm()
{
    if (_bgmHandle.IsValid && GameAudio.IsPlaying(_bgmHandle))
    {
        GameAudio.Stop(_bgmHandle, 0.25f);
        _bgmHandle = default;
        return;
    }

    PlayBgmAsync(OpenCancellationToken, _audioScope).Forget();
}

async UniTask PlayBgmAsync(CancellationToken token, uint scope)
{
    AudioPlayResult result = await GameAudio.TryPlayBgmAsync(
        GameAudioIds.LudoBgm,
        0.5f,
        token);

    if (scope != _audioScope || token.IsCancellationRequested)
    {
        if (result.IsPlaying && GameAudio.IsInited)
        {
            GameAudio.TryStop(result.Handle, 0.25f);
        }

        return;
    }

    if (result.IsPlaying)
    {
        _bgmHandle = result.Handle;
    }
}

void OnClose()
{
    _audioScope++;
    if (GameAudio.IsInited)
    {
        GameAudio.StopBgm(0.25f);
    }

    _bgmHandle = default;
}
```

完整可运行示例见 `TestMainPanel` 的音频按钮。
