# UIFrame 本机备份参考服务

Python 3.9+，无第三方依赖，默认监听本机。

```sh
python3 server.py init --root /tmp/uiframe-backup-local
python3 server.py serve --root /tmp/uiframe-backup-local
```

初始化生成数据目录中的 `credentials.json`，账号默认 `local-user`，访问令牌随机生成。将它填入 Unity 的 **Tools → UIFrame → 图片与备份**，地址默认 `http://127.0.0.1:8787`。凭据文件不要提交。

服务使用真实 SQLite / 文件存储，支持系统后台整文件上传并校验提交、分片上传、断点查询、SHA-256 校验、重复提交、账号隔离和下载。上限为单文件 512 MiB、分片 4 MiB；不是生产部署方案。停止服务不删除数据，重启时使用同一数据目录。

测试：

```sh
python3 -m unittest -v test_server.py
```

Unity 显式 HTTP 集成测试：

```sh
python3 integration_server.py
```

它在 `127.0.0.1:18787` 启动临时测试服务，然后显式运行 Unity `ReceiptDownloadSurvivesHistoryCleanupAndReopen`。测试服务停止后删除测试数据；固定令牌仅用于这一本机测试。

接口和客户端用法见包内 `Docs/Gallery.md`。真机需要使用电脑可达的局域网地址；不会自动放开手机 HTTP 策略或把本机服务部署到公网。

后台扩展：先建立上传会话，再 `PUT /v1/uploads/{key}/background`。正文为整个文件，必须带 Bearer 认证和 `X-Backup-Account-SHA256`（账号 UTF-8 的 SHA-256）。校验成功并持久提交后才返回 completed；重复请求幂等，不承诺整文件上传的分片续传。传输中断仅清理本次临时文件，不覆盖已有分片。`.incoming` 文件创建前登记持久归属；显式 TTL 清理可恢复中断的回收意图，不删除已完成副本。

性能与能力协商：客户端在服务实例内复用服务器能力；`POST /v1/uploads` 的响应增加可选 `capabilities`，包含协议版本、账号、分片大小、文件上限与后台上传支持。无需逐张额外查询能力，也不代表免除服务器逐次认证和校验。当前接口仍逐张建立会话，批量清单接口尚未实现。

提交 SHA-256 与分片文件写入不持有 SQLite 全局锁。同一上传的修改由固定数量的分组锁协调，确保校验期间同一任务不能同时追加、删除或后台替换；其他组的上传与能力查询可继续进行。

未完成会话的过期回收默认关闭，显式开启：

```sh
python3 server.py serve --root /tmp/uiframe-backup-local --upload-ttl-days 7
# 已记录失败的清理，只有显式要求才再次执行：
python3 server.py serve --root /tmp/uiframe-backup-local --upload-ttl-days 7 --retry-failed-cleanup
```

活动上传 / 校验 / 提交持有会话占用，不参与 TTL。回收先持久认领，再删除临时文件并确认结果；旧会话返回410。重新登记同一幂等身份是显式操作，新 epoch 阻止旧请求迟到提交。完成回执和共享内容文件不受 TTL 影响。服务是单进程本机参考实现，生产认证、配额和灾备另行配置。
