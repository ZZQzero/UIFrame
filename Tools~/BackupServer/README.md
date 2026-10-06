# UIFrame 本机照片备份参考服务（v2）

运行服务只需 Python 3.9+ 标准库。业务 API 与私有上传分别监听 8787、8788：

```sh
python3 server.py init --root /tmp/uiframe-backup-local-v2
python3 server.py serve --root /tmp/uiframe-backup-local-v2
python3 -m unittest -v test_server.py
```

初始化创建仅当前用户可读的 credentials.json，包含 local-user 账号及随机令牌。通过应用的运行时配置提供令牌，不写进场景、日志或 Git。服务使用 SQLite 和普通文件，只有 v2 格式；需要新数据目录，不转换已有格式。重启使用相同目录可以恢复未完成的确认与清理。每个数据目录仅允许一个 Store／服务进程持有：在打开数据库与恢复写入前取得跨进程独占锁，重复启动（无论端口是否不同）直接失败且不改动已有上传。关闭时先停止接收并等待所有 HTTP 处理、后台工作和数据库关闭，再释放所有权；进程崩溃由操作系统释放锁。`.backup-owner` 是持久锁文件，不应手工删除。

流程为批量 Plan → 独立文件 PUT → 服务端核验 → 滚动批量 Query。取消使用独立批量 Cancel。没有客户端 commit，也没有逐张能力查询：

| 操作 | 接口 | 成功的含义 |
| --- | --- | --- |
| Plan | POST /v2/backup/plans | 返回逐项上传授权、确认回执或拒绝 |
| Query | POST /v2/backup/status | 返回逐项真实状态和确认回执 |
| Cancel | POST /v2/backup/cancellations | 原子仲裁取消；确认已经发生时返回回执 |
| 上传 | PUT 私有 origin 的 /objects/{uploadId} | 空 204，仅表示完整文件已持久接收 |
| 已备份列表 | GET /v2/backups?after=0&limit=32 | 账号内有限页 |
| 下载 | GET /v2/backups/{backupId}/content | 不可变内容；客户端校验后才发布目标文件 |

控制请求使用业务 Bearer，私有上传只使用描述中的 X-Upload-Authorization，明确拒绝业务 Authorization。固定 requestId 的参数不能变化。请求每批 1–32 项，上限 256 KiB；响应 512 KiB；上传描述 8 KiB；照片 512 MiB。上传流式处理，不全量载入内存。

业务及存储地址必须是可信的最终终点，服务、反向代理、CDN和认证网关均不得重定向这些请求。Desktop / Android 客户端拒绝自动跳转；iOS background NSURLSession 由系统自动跟随，客户端最终 URL / 可用 metrics 校验只能事后发现违规，不能阻止字节转发。正式接入须用设备核对302/307/308、同域及跨域行为；本机服务不发送重定向，不能代替该验收。

上传授权默认 24 小时有效。授权到期但尚未超过保留窗口时，可由客户端显式 Plan 更新描述；传输结果未知时先显式 Query 核对。正常 Pending / Verifying 返回下一检查时点（参考服务间隔 5 秒）。授权到期再过 24 小时的上传尝试进入过期维护候选；维护关闭后，新的尝试必须使用新代次。尚未关闭的尝试仍可由显式 Plan 续期；续期和维护关闭在同一事务锁下仲裁，旧过期候选不能清理已续期的文件。取消墓碑与请求身份保留，以阻止迟到请求重新创建已取消的尝试。

独立工作线程依据持久 ready 记录核验大小和 SHA-256，不依赖客户端继续发请求。 核验线程发生无法隔离的故障后，新Plan、未确认状态查询及新PUT返回503 ConfirmationUnavailable；已确认内容的查询／下载和取消仍可执行。在途PUT保存完完整文件后也报告503，已发布ready文件不会删除，显式重启后继续核验。关闭尝试数据库和目录锁释放后传播原故障，不自动重启线程或重试失败操作。核验通过后发布不可变文件，再提交备份记录。只有账号内已核验内容可以复用；重复签名 PUT 不会覆盖已经发布的内容。来源版本与内容对象分别记录，同内容的多个来源仍保留各自备份记录。

每个请求独占自己的 incoming 文件。流式接收期间不持有上传尝试锁，持久 writing 记录保护在用文件；核验只读取已关闭的 ready 文件。慢速重复 PUT 不会阻止已到达文件的核验和清理。登记、发布和过期维护使用相同的短锁顺序；维护跳过仍有写入者的尝试，并且仅在本轮条件更新实际关闭该尝试后清理它的 ready 文件，取消仍以最终发布时的仲裁状态为准。

临时文件创建前登记归属；启动时把已中断的写入转入清理。已完成处理的临时文件自动回收，超过 24 小时且没有备份引用的发布对象也可回收。每轮维护最多处理 64 个候选，活动文件操作持有的锁不会阻塞其他回收候选。已确认及仍被引用的内容不受临时 TTL 影响。单个文件清理失败持久隔离，其余上传、核验、清理继续；修复磁盘问题后显式执行：

```sh
python3 server.py serve --root /tmp/uiframe-backup-local-v2 --retry-failed-cleanup
```

Unity 的 HTTP 集成测试使用独立临时服务：

```sh
python3 integration_server.py
```

它监听 127.0.0.1:18787 和 :18788，固定测试账号 integration-user。测试令牌只用于本机夹具。停止进程后临时目录会删除。运行 Unity 的 MediaIntegration 类别时保持服务开启。

手机访问电脑时，使用可达的局域网地址，例如：

```sh
python3 server.py serve --root /tmp/uiframe-backup-local-v2 --host 0.0.0.0 --storage-origin http://192.168.1.2:8788
```

客户端业务地址使用 http://192.168.1.2:8787，显式启用开发 HTTP，并配置平台网络规则。0.0.0.0 是监听地址，不能作为手机收到的上传地址。

机器契约见包内 Docs/GalleryBackupProtocol.openapi.yaml，固定示例见 fixtures。此服务用于本机协议与故障验证。生产接入仍需真实认证、HTTPS、配额、对象存储、监控、灾备及容量验收；本机测试不构成生产吞吐保证。
