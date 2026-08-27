# 红点系统使用说明

红点系统位于 `UIFrame` 命名空间，用于统计功能入口、页签和按钮的未读数量。

系统使用路径表示父子关系：

```text
Mail
├── Mail/Inbox
└── Mail/System
```

业务只设置叶子节点的真实数量，父节点由系统自动求和。

```csharp
RedDot.Set("Mail/Inbox", 3);
RedDot.Set("Mail/System", 2);

int mailCount = RedDot.Get("Mail"); // 5
```

## 1. 基本使用

引用命名空间：

```csharp
using UIFrame;
```

### 设置真实数量

```csharp
RedDot.Set("Mail/Inbox", unreadCount);
```

数量必须大于等于 0。重复设置相同数量不会产生通知。

红点数量应来自业务数据，而不是由红点系统保存业务状态：

```csharp
public sealed class MailModel
{
    private readonly HashSet<long> unreadMailIds = new();

    public void ApplyMailList(IReadOnlyList<MailData> mails)
    {
        unreadMailIds.Clear();

        for (int i = 0; i < mails.Count; i++)
        {
            if (!mails[i].IsRead)
            {
                unreadMailIds.Add(mails[i].Id);
            }
        }

        RedDot.Set("Mail/Inbox", unreadMailIds.Count);
    }

    public void MarkAsRead(long mailId)
    {
        unreadMailIds.Remove(mailId);
        RedDot.Set("Mail/Inbox", unreadMailIds.Count);
    }
}
```

如果后端直接返回未读数量，应直接使用后端结果：

```csharp
MailSummary response = await mailApi.GetSummaryAsync();

await UniTask.SwitchToMainThread();
RedDot.Set("Mail/Inbox", response.UnreadCount);
```

不要使用多次加减模拟最终数量。每次业务状态变化后重新设置真实总数，可以避免漏消息、断线重连或重复回调导致统计失真。

### 获取数量

```csharp
int inboxCount = RedDot.Get("Mail/Inbox");
int totalMailCount = RedDot.Get("Mail");
```

路径不存在时返回 0。

### 删除动态功能

```csharp
RedDot.Remove("Activity/Summer");
```

`Remove` 会删除指定路径及其所有后代，并自动回扣祖先数量。

适用于活动结束、功能关闭或动态模块卸载。删除不存在的路径不会报错。

### 清理账号数据

```csharp
RedDot.Clear();
```

建议在登出、切号时调用。

`Clear` 会：

- 清除全部红点数据；
- 保留当前监听；
- 在下一次通知时把打开中的 UI 更新为 0；
- 允许下一账号重新调用 `Set` 后继续刷新现有 UI。

## 2. UI 绑定

使用 `RedDotView` 自动控制红点显示。

推荐层级：

```text
MailButton                  ← 挂 RedDotView
└── RedDot                  ← 配置为 target
    └── CountText           ← 可选 TMP_Text
```

Inspector 配置：

- `Path`：红点路径，例如 `Mail/Inbox`；
- `Target`：要显示和隐藏的红点对象；
- `Tmp Count Text`：可选的 TextMeshPro 数字文本；
- `Max Count`：最大显示数字，默认 99，超过后显示 `99+`；设置为 0 表示不限制。

只显示圆点、不显示数字时，不配置 `Tmp Count Text` 即可。

### Target 限制

`RedDotView` 必须挂在一个持续激活的宿主对象上。

`Target` 可以是：

- 宿主的子节点；
- 不会影响宿主激活状态的兄弟或独立对象。

`Target` 不能是：

- `RedDotView` 所在的 GameObject；
- `RedDotView` 所在对象的祖先。

如果隐藏 Target 会同时禁用宿主，`RedDotView` 将退订，之后无法通过红点变化重新显示。

### 动态切换路径

循环使用的 UI Item 可以切换监听路径：

```csharp
redDotView.SetPath("Task/Daily");
```

组件处于激活状态时，会先验证新路径，再解除旧监听并绑定新路径。绑定后会立即刷新当前数量。

## 3. 手动监听

不使用 `RedDotView` 时，可以直接监听：

```csharp
private void OnEnable()
{
    RedDot.Bind("Mail", OnMailRedDotChanged);
}

private void OnDisable()
{
    RedDot.Unbind("Mail", OnMailRedDotChanged);
}

private void OnMailRedDotChanged(int count)
{
    mailRedDot.SetActive(count > 0);
}
```

注意：

- `Bind` 后会立即回调一次当前值；
- 重复绑定相同回调不会重复添加；
- 必须使用同一个委托实例解除监听；
- 建议在 `OnEnable` 绑定，在 `OnDisable` 解绑；
- 单个回调抛异常时会记录异常，但不会阻断其他回调。

不要使用两个不同的匿名函数进行绑定和解绑：

```csharp
// 错误：这是两个不同的委托实例
RedDot.Bind("Mail", value => Refresh(value));
RedDot.Unbind("Mail", value => Refresh(value));
```

应保存委托，或者使用成员方法：

```csharp
RedDot.Bind("Mail", Refresh);
RedDot.Unbind("Mail", Refresh);
```

## 4. 路径规则

路径使用 `/` 分段：

```text
合法：Mail
合法：Mail/Inbox
合法：Activity/Summer/Task

非法：/Mail
非法：Mail/
非法：Mail//Inbox
```

建议在游戏业务层统一定义常量，避免字符串拼写错误：

```csharp
public static class RedDotKeys
{
    public const string Mail = "Mail";
    public const string MailInbox = "Mail/Inbox";
    public const string MailSystem = "Mail/System";
}
```

使用：

```csharp
RedDot.Set(RedDotKeys.MailInbox, unreadCount);
```

## 5. 叶子与父节点规则

只有叶子节点允许调用 `Set`：

```csharp
RedDot.Set("Mail/Inbox", 3);
RedDot.Set("Mail/System", 2);

// 错误：Mail 已经是父节点
RedDot.Set("Mail", 5);
```

父节点只能通过 `Get` 或 `Bind` 获取聚合值。

如果入口自身也有一个独立红点，应增加专用叶子：

```text
Mail
├── Mail/Inbox
├── Mail/System
└── Mail/Unlock
```

```csharp
RedDot.Set("Mail/Unlock", canUnlock ? 1 : 0);
```

不能在非零叶子下直接创建子节点：

```csharp
RedDot.Set("Activity", 1);

// 错误：Activity 当前是值为 1 的叶子
RedDot.Set("Activity/Summer", 1);
```

应先清除旧语义，再使用新的层级：

```csharp
RedDot.Set("Activity", 0);
RedDot.Set("Activity/Summer", 1);
```

叶子设置为 0 后，空节点会自动回收；监听关系不受影响。

## 6. 列表项红点

不建议把大量实体 ID 放入全局红点树：

```csharp
// 不推荐：邮件数量很大时会创建大量动态节点
RedDot.Set($"Mail/{mail.Id}", mail.IsRead ? 0 : 1);
```

列表 Cell 应直接读取自身业务数据：

```csharp
public void SetData(MailData mail)
{
    unreadDot.SetActive(!mail.IsRead);
}
```

全局树只保存功能级统计：

```csharp
RedDot.Set("Mail/Inbox", mailModel.UnreadCount);
```

## 7. 通知时机

`Set` 会立即更新数据：

```csharp
RedDot.Set("Mail/Inbox", 3);
int count = RedDot.Get("Mail"); // 立即得到最新值
```

UI 回调会在帧末统一派发：

- 同一帧多次修改同一路径，只派发最终批次；
- 多个叶子变化时，父节点只进入一次通知队列；
- 回调中产生的新变化会进入下一批；
- Play 模式由内部 Runner 自动调用，不需要初始化；
- 不需要手动调用 `Flush`。

EditMode 单元测试中可以手动派发：

```csharp
RedDot.Set("Mail/Inbox", 3);
RedDot.Flush();
```

业务代码通常不应主动调用 `Flush`。

## 8. 主线程要求

所有红点 API 必须在 Unity 主线程调用。

网络响应、推送或后台任务更新红点前，应先切回主线程：

```csharp
await UniTask.SwitchToMainThread();
RedDot.Set("Mail/Inbox", unreadCount);
```

不要从后台线程直接操作：

```csharp
// 错误
Task.Run(() => RedDot.Set("Mail/Inbox", unreadCount));
```

Editor 和 Development Build 会主动检查线程并抛出明确异常。

## 9. 推荐业务流程

```text
服务器响应或推送
        ↓
业务 Model 更新真实状态
        ↓
计算当前真实数量
        ↓
RedDot.Set(叶子路径, 真实数量)
        ↓
父节点自动聚合
        ↓
帧末通知 RedDotView
```

核心原则：

1. 业务 Model 是真实数据源；
2. 红点系统只保存派生数量；
3. 使用绝对数量调用 `Set`；
4. 只设置叶子，不设置父节点；
5. 动态功能结束时调用 `Remove`；
6. 登出或切号时调用 `Clear`；
7. 所有操作在 Unity 主线程执行。
