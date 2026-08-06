# Local Theme Extractor - Emby 插件

从本地媒体文件中提取主题曲的 Emby 插件，无需依赖任何外部下载服务。

[English](README_EN.md)

## 功能

- **电视剧片头提取**：利用 Emby 已有的片头标记（Intro Markers），用 ffmpeg 提取片头音乐作为 `theme.mp3`；默认从 S01E02 采样（第 1 集常有冷开场/无 OP），该集无合格标记则整部剧跳过
- **电视剧片尾回退**：对没有片头标记的电视剧，自动检测片尾并提取片尾音乐
- **电影片尾提取**：通过音频静音检测和视频黑帧检测定位片尾起始点，提取片尾音乐
- **固定窗口兜底**：对无黑帧、无静音的电影，可选提取最后 N 秒
- **三档失败策略**：失败 1-2 次正常重试 → 3-5 次截取最后 30s → 6 次彻底跳过

## 特性

- **零外部依赖**：仅使用 Emby 自带的 ffmpeg，不需要网络下载，不依赖 SkiaSharp 或任何图像库
- **内存管道处理**：ffmpeg 通过 `pipe:1` 输出到内存，无中间临时文件
- **带宽优化**：使用 `-map 0:a:0` 仅读取音频流，对网盘/CloudDrive FUSE 挂载大幅节省流量
- **三级检测策略**：silencedetect（~2MB）→ blackdetect（~200MB）→ 固定窗口（0 额外 I/O）
- **GPU 加速**：blackdetect 支持 nvdec / vaapi / videotoolbox / dxva2
- **只读文件系统感知**：自动检测并跳过不可写的挂载点，避免浪费检测流量
- **失败持久化记录**：失败计数保存到 `/config/data/lte-failures.json`，跨任务运行保留
- **支持 .strm 文件**：自动解析 strm 内容获取实际媒体路径
- **多媒体库选择**：GenericEdit UI 界面，支持多选媒体库
- **完成后自动扫库**：本次有新写出 theme.mp3 时自动触发媒体库扫描，Emby 立即登记新主题曲（空跑不扫）
- **48 kHz 输出**：MP3 保持影视音轨原生的 48 kHz 采样率，不再降到 44.1 kHz

## 安装

1. 从 [Releases](../../releases) 下载 `LocalThemeExtractor.dll`
2. 将 DLL 复制到 Emby 的插件目录：
   - Docker: `/config/plugins/`
   - Windows: `%AppData%\Emby-Server\plugins\`
   - Linux: `/var/lib/emby/plugins/`
3. 重启 Emby Server
4. 在 Emby 管理后台 → 插件 → Local Theme Extractor 中配置
5. 在 Emby 管理后台 → 计划任务 → 手动运行「提取本地主题曲」

## 配置说明

| 选项 | 说明 | 默认值 |
|------|------|--------|
| 媒体库范围 | 选择要处理的媒体库（不选 = 全部） | 全部 |
| 并行线程数 | 同时处理数量（1 = 顺序执行） | 1 |
| 每项延迟 | 顺序执行时每项之间的延迟秒数 | 2 |
| 覆盖已有 | 已存在 theme.mp3 时是否重新提取 | 否 |
| **电视剧** | | |
| 提取片头 | 使用 Emby 片头标记提取主题曲 | 是 |
| 优先季数 | 从第几季采样（0 = 最早有标记的季；无季号的动画须设 0） | 1 |
| 采样集数 | 从目标季的第几集采样；该集无合格标记则整部剧跳过 | 2 |
| 最短片头秒数 | 低于此值视为广告，跳过 | 20 |
| 片尾回退 | 无片头标记时尝试检测片尾提取 | 否 |
| **电影** | | |
| 片尾检测 | 检测电影片尾黑帧/静音并提取 | 是 |
| 片尾扫描秒数 | 从片尾往前扫描多少秒 | 600 |
| 片尾最短秒数 | 检测到的片尾低于此值不提取 | 30 |
| 固定窗口回退 | 检测失败时提取最后 N 秒 | 否 |
| 回退窗口秒数 | 固定窗口提取长度 | 90 |
| **硬件/输出** | | |
| 硬件解码 | 启用 GPU 加速（blackdetect 时有效） | 否 |
| 解码器名称 | nvdec / vaapi / videotoolbox / dxva2 | nvdec |
| 音频码率 | 输出 MP3 码率 | 192 kbps |

## 检测策略

```
电视剧（有片头标记）：
  只采「优先季 × 采样集数」那一集（默认 S01E02）→ theme.mp3
  该集不存在或标记不合格 → 整部剧跳过（也不参与片尾回退）

电视剧（无片头标记，启用回退）：
  silencedetect → blackdetect → 跳过

电影：
  silencedetect（音频检测，~2MB 流量）
    ↓ 命中 → 提取
    ↓ 未命中
  blackdetect（视频检测，~200MB 流量）
    ↓ 命中 → 提取
    ↓ 未命中
  固定窗口（可选，0 额外流量）
    ↓ 启用 → 提取最后 N 秒
    ↓ 未启用 → 记录失败
```

## 失败重试机制

失败记录保存在 `/config/data/lte-failures.json`，跨任务运行保留。

| 失败次数 | 行为 |
|---------|------|
| 1-2 次 | 下次运行正常重试（完整检测流程） |
| 3-5 次 | 跳过检测，直接截取最后 30 秒 |
| 6+ 次 | 彻底跳过（源文件可能有问题） |
| 成功 | 清除失败记录 |

删除 `lte-failures.json` 可重置所有记录。

## 构建

```bash
dotnet build src/LocalThemeExtractor.csproj -c Release
```

输出：`src/bin/Release/netstandard2.0/LocalThemeExtractor.dll`

> 注意：构建需要 Emby SDK NuGet 包（MediaBrowser.Common 4.8.0.24-beta、MediaBrowser.Server.Core 4.8.0.24-beta）。

## 兼容性

- Emby Server 4.7+（netstandard2.0）
- Linux / Windows / macOS
- Docker 容器（linuxserver/emby 等）
- 有无 GPU 均可（UseHwDecode 默认关闭）
- 不依赖 SkiaSharp 或任何图像处理库

## License

[MIT](LICENSE)
