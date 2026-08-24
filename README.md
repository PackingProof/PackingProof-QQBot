# PackingProof QQBot

独立运行在 Windows 的 QQ 官方机器人适配器，支持 QQ 私聊和 QQ 群。它只通过 PackingProof 扩展 API 检索和下载录像，不访问 PackingProof 的数据库、录像目录或 NAS 凭据。

## 给第一次使用的用户

请直接阅读 [新手指南](docs/新手指南.md)。它包含以下完整流程：

- 在 QQ 开放平台创建机器人、取得 AppID 和 AppSecret
- 添加开发体验用户与确认 QQ 群能力
- 开启 PackingProof 扩展 API，并正确填写本机或局域网地址
- 首次配置、私聊查询、QQ 群白名单、续发录像和视频转码设置
- 常见错误、安全注意事项与更新方式

Windows 发布包中也会附带同一份《使用说明》，无需先安装 .NET 或 FFmpeg。首次双击“启动机器人”会自动进入配置，完成后立即连接 QQ。

## 0.0.1 功能

- 官方 QQ WebSocket 私聊与 QQ 群 `@机器人 单号` 查询
- 先回复录像数量、时间、时长和大小，再上传录像
- 每批最多 3 段录像，回复“继续”可发送下一批
- 原片不超限时直接发送，超限时由 PackingProof 生成临时交付副本
- 默认保持源编码并动态降低码率，也可在设置中选择 H.265
- QQ AppSecret 与 PackingProof 扩展凭据使用 Windows 当前用户加密保存

## 视频发送设置

如需由管理员统一下发设置，也可以编辑状态目录中的 `settings.json`。其中不含 QQ AppSecret 或扩展凭据；这些敏感数据保存在单独的 Windows 加密文件中。

```json
{
  "deliveryMaxSizeMb": 190,
  "deliveryProfile": "source_codec_target_size"
}
```

- `deliveryMaxSizeMb`：1 到 200，默认 190。原片未超过该值时直接转发
- `source_codec_target_size`：默认选项，保持源视频编码，主机按录像时长动态计算码率
- `h265_target_size`：明确请求主机生成 H.265 副本

转码由 PackingProof 主机完成；QQ 适配器不打包 FFmpeg，也不会修改原始录像。副本文件名为原文件名加 `_转码.mp4`，无法在不切割的前提下压入限制时会在聊天中说明原因。
