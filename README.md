# UIFrame

基于 URP、UGUI、YooAsset 和 UniTask 的轻量 Unity UI 框架，提供面板生命周期、层级栈、缓存、异步加载、URP Camera Stack、屏幕方向管理和编辑器绑定代码生成。

## 环境要求

- Unity 6000.0 或更高版本
- UniTask 2.5.10 或更高版本
- YooAsset 3.0.5 或更高版本
- UGUI 2.0.0 或更高版本
- Universal RP 17.3.0 或更高版本

## 安装

通过 Unity Package Manager 的 **Add package from git URL** 添加：

```text
https://github.com/ZZQzero/UIFrame.git
```

如果项目尚未安装 UniTask 和 YooAsset，请先在 `Packages/manifest.json` 中配置 OpenUPM：

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.cysharp",
        "com.tuyoogame"
      ]
    }
  ],
  "dependencies": {
    "com.cysharp.unitask": "2.5.10",
    "com.tuyoogame.yooasset": "3.0.5",
    "com.zzq.uiframe": "https://github.com/ZZQzero/UIFrame.git"
  }
}
```

## 快速开始

```csharp
using Cysharp.Threading.Tasks;
using UIFrame;
using YooAsset;

public sealed class MainPanel : UIPanel<UINone>
{
    protected override void OnOpen(UINone args)
    {
    }
}

public static class UIStartup
{
    public static async UniTask StartAsync(ResourcePackage package)
    {
        UI.Init(package);
        UI.Register<MainPanel>("MainPanel");
        await UI.Push<MainPanel>();
    }
}
```

不再使用时调用：

```csharp
UI.Shutdown();
```

## URP Camera Stack

`UI.Init()` 后 Canvas 默认为 `Screen Space Camera`，并绑定常驻 UI Camera；同时开启 `Vertex Color Always In Gamma Color Space`。

需要叠加到主相机时：

```csharp
UI.ConfigureURPCameraStack(); // Base = Camera.main，复用已有 UI Camera
```

也可指定 Base / 外部 UI Camera：

```csharp
UI.ConfigureURPCameraStack(baseCamera, existingUICamera);
```

行为约定：

- 只把 UI Camera 设为 `Overlay` 并加入 Base Camera Stack
- **不改动** Base Camera 的 cullingMask、renderType 等
- `Disable` 只从 Stack 移除 UI Camera；Canvas 仍保持 `Screen Space Camera` 与 UI Camera 引用
- UI Camera 创建后常驻，不销毁

```csharp
UI.DisableURPCameraStack();
```

## 目录

- `Runtime/Core`：面板 API、生命周期、栈与缓存
- `Runtime/Load`：YooAsset 异步加载
- `Runtime/Root`：运行时 Canvas 与 UI 层
- `Runtime/Screen`：屏幕方向和 Canvas 布局同步
- `Editor`：面板脚本与绑定代码生成工具

## License

[MIT](LICENSE)
