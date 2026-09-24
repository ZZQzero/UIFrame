# 生命周期作用域

`UIPanel` 提供两个作用域：

- `OpenScope`：当前打开周期使用，关闭或再次打开时释放。
- `LifetimeScope`：当前面板实例使用，销毁时释放；缓存面板再次打开时仍然有效。

作用域可以登记取消令牌、事件订阅、计时器和其他资源：

```csharp
protected override void OnOpen(UINone args)
{
    OpenScope.Subscribe<BagChanged>(_ => Refresh());
    OpenScope.Schedule(
        TimerOptions.Repeat(0, 1000),
        static (in TimerContext _) => { });

    LoadAsync(OpenScope.Token).Forget();
}

protected override void OnCreate()
{
    LifetimeScope.Register(new MyResource());
}
```

也可以登记任意清理动作：

```csharp
OpenScope.Register(() => view.Clear());
```

作用域只清理通过它登记的内容。`OnCreate` 中的长期监听放入 `LifetimeScope`，每次打开产生的订阅、计时器和异步任务放入 `OpenScope`。
