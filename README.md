# PackingProof QQBot

独立运行在 Windows 的 QQ 官方机器人适配器，支持 QQ 私聊；开通群聊能力后也支持 QQ 群。它只通过 PackingProof 扩展 API 检索和下载录像，不访问 PackingProof 的数据库、录像目录或 NAS 凭据。

## 状态

当前仓库包含可配置的首版实现。需要在 Windows 上通过 QQ 开放平台创建机器人，并在 PackingProof 中批准 `recordings.search`、`recordings.download` 和 `recordings.delivery` 权限。

## 只需四步

1. 下载并解压 Windows 发布包，不需要安装 .NET 或 FFmpeg
2. 双击“配置机器人”，输入 QQ AppID、AppSecret 和 PackingProof 地址；在 PackingProof 弹窗中批准授权
3. 在 QQ 开放平台的“开发设置”添加自己的 QQ 号为开发体验用户，双击“启动机器人”后在 QQ 私聊中向机器人发送一个单号即可查询录像
4. 开通 QQ 群聊能力后，也可把机器人拉进目标群并 @机器人发一个单号；控制台会显示群 OpenID，关闭机器人后双击“添加群白名单”并粘贴即可启用该群

视频大小与编码不需要改 JSON：双击“视频发送设置”，按提示选择大小和编码即可。敏感信息使用 Windows 加密保存。

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
