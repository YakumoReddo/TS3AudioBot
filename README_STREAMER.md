# FLAC to TCP Streamer 使用说明

## 简介

本脚本用于读取FLAC音频文件，将其编码为Opus格式，并通过TCP协议发送到TS3AudioBot服务器，最终播放到Teamspeak 3。

## 前置要求

### 系统依赖

```bash
# Debian/Ubuntu
sudo apt-get update
sudo apt-get install -y libopus0 ffmpeg python3-pip

# macOS
brew install opus ffmpeg

# Windows
# 1. 从 https://opus-codec.org 下载 opus.dll
# 2. 将 opus.dll 放到系统 PATH 或程序目录
```

### Python依赖

```bash
pip install -r requirements.txt
```

或手动安装：

```bash
pip install librosa opuslib numpy
```

## 使用方法

### 基本用法

```bash
python3 flac_to_tcp_streamer.py /path/to/your/music.flac
```

### 指定服务器地址和端口

```bash
python3 flac_to_tcp_streamer.py /path/to/music.flac --host 192.168.1.100 --port 9001
```

### 参数说明

| 参数 | 短选项 | 默认值 | 说明 |
|------|--------|--------|------|
| `flac_file` | 必填 | - | FLAC文件路径 |
| `--host` | `-H` | `localhost` | TS3AudioBot服务器地址 |
| `--port` | `-p` | `9001` | TCP服务端口 |

## TS3AudioBot配置

确保你的TS3AudioBot已启用TCP音频服务器，配置示例：

```toml
# bots/yourbot.toml
[audio.tcp_server]
enabled = true
port = 9001
send_audio = true
receive_audio = true
```

## 工作原理

```
┌─────────┐     ┌──────────────┐     ┌─────────────┐     ┌──────────────┐     ┌────────┐
│  FLAC   │ ──► │  librosa    │ ──► │  opuslib    │ ──► │  TCP Packet  │ ──► │ TS3AB  │
│  File   │     │  (解码PCM)   │     │  (编码Opus) │     │  [4B len][1B │     │ Server │
└─────────┘     └──────────────┘     └─────────────┘     │  codec][Data]│     └────────┘
                                                         └─────────────┘
```

### 协议格式

每个数据包格式：

```
[Length: 4字节][Codec: 1字节][Audio Data: N字节]
```

- **Length**: 数据长度（小端int32）
- **Codec**: 1 = OpusMusic
- **Audio Data**: Opus编码的音频数据

### 音频规格

- **采样率**: 48,000 Hz
- **声道**: 2 (立体声)
- **帧大小**: 960 样本 (20ms)
- **编码器**: Opus @ 48kbps

## 示例

### 1. 播放本地FLAC文件

```bash
# 连接到本地TS3AudioBot
python3 flac_to_tcp_streamer.py ~/Music/song.flac

# 连接到远程TS3AudioBot
python3 flac_to_tcp_streamer.py ~/Music/song.flac -H 192.168.1.50 -p 9001
```

### 2. 在脚本中调用

```python
from flac_to_tcp_streamer import FlacTcpStreamer

streamer = FlacTcpStreamer("localhost", 9001, "/path/to/music.flac")
if streamer.connect():
    streamer.stream()
    streamer.close()
```

## 故障排除

### 连接被拒绝

1. 确认TS3AudioBot已启动并连接到Teamspeak
2. 检查TCP服务器是否启用：`tcp_server.enabled = true`
3. 验证端口号是否正确

### 音频无法播放

1. 确保bot正在运行且已连接
2. 检查防火墙是否允许该端口
3. 确认没有其他客户端占用端口

### 依赖问题

```bash
# 重新安装依赖
pip uninstall librosa opuslib numpy
pip install librosa opuslib numpy

# 如果librosa安装失败，尝试安装系统依赖
sudo apt-get install libsndfile1 ffmpeg
```

### 编码错误

某些FLAC文件可能包含librosa无法处理的特殊编码，尝试使用ffmpeg转换：

```bash
ffmpeg -i input.flac -ar 48000 -ac 2 output_converted.flac
```

## 性能优化

### 大文件处理

脚本采用流式处理，内存占用低。理论上一小时立体声48kHz FLAC约占用：
- 实时内存: ~10-50 MB
- 临时缓存: ~2 MB

### 网络优化

- 使用 `sendall()` 确保数据完整性
- 20ms帧大小实现低延迟播放
- 进度显示帮助监控传输状态

## 许可证

本脚本随TS3AudioBot项目一起发布，遵循OSL-3.0许可证。
