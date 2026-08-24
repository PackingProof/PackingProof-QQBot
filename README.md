# PackingProof QQ 群机器人

独立运行在 Windows 的 QQ 官方群机器人适配器。它只通过 PackingProof 扩展 API 检索和下载录像，不访问 PackingProof 的数据库、录像目录或 NAS 凭据。

## 状态

当前仓库包含可配置的首版实现。需要在 Windows 上通过 QQ 开放平台创建机器人，并在 PackingProof 中批准 `recordings.search`、`recordings.download` 和 `recordings.delivery` 权限。

## 交付副本设置

首次执行 `--configure` 后，可编辑状态目录中的 `settings.json`。其中不含 QQ AppSecret 或扩展凭据；这些敏感数据保存在单独的 DPAPI 保护文件中。

```json
{
  "deliveryMaxSizeMb": 190,
  "deliveryProfile": "source_codec_target_size"
}
```

- `deliveryMaxSizeMb`：1 到 200，默认 190。原片未超过该值时直接转发
- `source_codec_target_size`：默认选项，保持源视频编码，主机按录像时长动态计算码率
- `h265_target_size`：明确请求主机生成 H.265 副本

转码由 PackingProof 主机完成；QQ 适配器不打包 FFmpeg，也不会修改原始录像。副本文件名为原文件名加 `_转码.mp4`，无法在不切割的前提下压入限制时会在群内说明原因。
