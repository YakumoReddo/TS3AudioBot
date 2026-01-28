#!/usr/bin/env python3
"""
Audio File to TCP Streamer for TS3AudioBot

读取音频文件(FLAC/WAV/MP3等)，编码为Opus，通过TCP发送到TS3AudioBot服务器进行播放。
支持格式: FLAC, WAV, MP3, OGG等 (通过soundfile或librosa)
"""

import socket
import struct
import sys
import time
import argparse
import array

# Packet types for the new protocol
PACKET_AUDIO_FROM_CLIENT = 2

# Codec types (must match TSLib/TsEnums.cs Codec enum)
# SpeexNarrowband = 0
# SpeexWideband = 1
# SpeexUltraWideband = 2
# CeltMono = 3
# OpusVoice = 4  (mono, 48kHz)
# OpusMusic = 5  (stereo, 48kHz)
CODEC_OPUS_VOICE = 4  # Mono
CODEC_OPUS_MUSIC = 5  # Stereo


def load_audio_file(file_path, target_sr=48000, stereo=True):
    """
    Load audio file and convert to target sample rate.
    Returns: (audio_data as numpy array, sample_rate, channels)
    """
    import numpy as np
    
    # Try soundfile first (better for WAV/FLAC)
    try:
        import soundfile as sf
        audio, sr = sf.read(file_path, dtype='float32')
        print(f"[soundfile] Loaded: {file_path}")
        
        # Handle mono/stereo conversion
        if len(audio.shape) == 1:
            # Mono file
            if stereo:
                audio = np.column_stack([audio, audio])  # Duplicate to stereo
            else:
                audio = audio.reshape(-1, 1)
        elif audio.shape[1] == 1 and stereo:
            audio = np.column_stack([audio[:, 0], audio[:, 0]])
        elif audio.shape[1] > 2:
            audio = audio[:, :2]  # Take first 2 channels
        
        # Resample if needed
        if sr != target_sr:
            try:
                import librosa
                if len(audio.shape) == 2 and audio.shape[1] == 2:
                    # Resample each channel separately for stereo
                    left = librosa.resample(audio[:, 0], orig_sr=sr, target_sr=target_sr)
                    right = librosa.resample(audio[:, 1], orig_sr=sr, target_sr=target_sr)
                    audio = np.column_stack([left, right])
                else:
                    audio = librosa.resample(audio.flatten(), orig_sr=sr, target_sr=target_sr)
                    if stereo:
                        audio = np.column_stack([audio, audio])
                    else:
                        audio = audio.reshape(-1, 1)
                sr = target_sr
                print(f"[librosa] Resampled to {target_sr}Hz")
            except ImportError:
                print(f"Warning: librosa not installed, using original sample rate {sr}")
        
        channels = audio.shape[1] if len(audio.shape) == 2 else 1
        return audio, sr, channels
        
    except ImportError:
        pass
    
    # Fall back to librosa
    try:
        import librosa
        audio, sr = librosa.load(file_path, sr=target_sr, mono=not stereo, dtype='float32')
        print(f"[librosa] Loaded: {file_path}")
        
        if len(audio.shape) == 1:
            if stereo:
                import numpy as np
                audio = np.column_stack([audio, audio])
            else:
                audio = audio.reshape(-1, 1)
        elif audio.shape[0] == 2:  # librosa returns (channels, samples)
            audio = audio.T  # Convert to (samples, channels)
        
        channels = audio.shape[1] if len(audio.shape) == 2 else 1
        return audio, sr, channels
        
    except ImportError:
        print("ERROR: Neither soundfile nor librosa is installed.")
        print("Install with: pip install soundfile librosa")
        sys.exit(1)


class AudioTcpStreamer:
    def __init__(self, host: str, port: int, audio_path: str):
        self.host = host
        self.port = port
        self.audio_path = audio_path
        self.socket = None
        self.encoder = None
        self.sample_rate = 48000
        self.channels = 2
        self.frame_size = 960  # 20ms at 48kHz
        self.bitrate = 96000   # Higher bitrate for music quality

    def connect(self) -> bool:
        """连接到TS3AudioBot TCP服务器"""
        try:
            self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.socket.connect((self.host, self.port))
            self.socket.settimeout(10.0)
            print(f"已连接到 {self.host}:{self.port}")
            return True
        except Exception as e:
            print(f"连接失败: {e}")
            return False

    def init_encoder(self, channels: int):
        """初始化Opus编码器"""
        import opuslib
        self.channels = channels
        self.encoder = opuslib.Encoder(
            self.sample_rate,
            self.channels,
            opuslib.APPLICATION_AUDIO
        )
        self.encoder.bitrate = self.bitrate
        print(f"Opus编码器初始化完成 - 采样率: {self.sample_rate}Hz, 声道: {self.channels}, 比特率: {self.bitrate}bps")

    def send_packet(self, audio_data: bytes, codec: int = CODEC_OPUS_MUSIC):
        """
        发送音频包 (新协议格式)
        协议格式: [Length:4字节][PacketType:1字节=2][Codec:1字节][Audio Data]
        """
        if not self.socket:
            raise RuntimeError("未建立TCP连接")
        
        # Length = PacketType(1) + Codec(1) + AudioData(N)
        length = 1 + 1 + len(audio_data)
        packet = struct.pack('<I', length) + bytes([PACKET_AUDIO_FROM_CLIENT, codec]) + audio_data
        self.socket.sendall(packet)

    def stream(self) -> bool:
        """流式发送音频文件"""
        import numpy as np
        
        try:
            print(f"正在加载音频文件: {self.audio_path}")
            audio, sr, channels = load_audio_file(
                self.audio_path,
                target_sr=self.sample_rate,
                stereo=True  # Always use stereo for music
            )
            
            duration = len(audio) / sr
            print(f"音频加载完成 - 采样率: {sr}Hz, 声道: {channels}, 时长: {duration:.2f}秒")

            # Initialize encoder with correct channel count
            self.init_encoder(channels)
            
            # Determine codec based on channels
            codec = CODEC_OPUS_MUSIC if channels == 2 else CODEC_OPUS_VOICE

            total_samples = len(audio)
            sent_packets = 0
            total_bytes = 0
            start_time = time.time()

            print("开始流式发送音频...")
            print("(按 Ctrl+C 停止)")

            # Calculate timing for real-time playback
            frame_duration = self.frame_size / self.sample_rate  # 20ms

            for offset in range(0, total_samples, self.frame_size):
                frame_start_time = time.time()
                
                frame = audio[offset:offset + self.frame_size]

                # Pad last frame if necessary
                if len(frame) < self.frame_size:
                    pad_length = self.frame_size - len(frame)
                    if len(frame.shape) == 2:
                        frame = np.vstack([frame, np.zeros((pad_length, frame.shape[1]), dtype=np.float32)])
                    else:
                        frame = np.concatenate([frame, np.zeros(pad_length, dtype=np.float32)])

                try:
                    # Convert float32 [-1.0, 1.0] to int16
                    frame_int16 = (np.clip(frame, -1.0, 1.0) * 32767).astype(np.int16)
                    
                    # Interleave stereo samples if needed (should already be interleaved from load)
                    if len(frame_int16.shape) == 2:
                        # Shape is (samples, channels), need to interleave
                        interleaved = frame_int16.flatten('C')  # Row-major flatten for interleaving
                    else:
                        interleaved = frame_int16
                    
                    # Encode to Opus
                    opus_data = self.encoder.encode(interleaved.tobytes(), self.frame_size)
                    self.send_packet(opus_data, codec=codec)

                    sent_packets += 1
                    total_bytes += len(opus_data)

                    if sent_packets % 50 == 0:
                        elapsed = time.time() - start_time
                        progress = (offset + self.frame_size) / total_samples * 100
                        current_time = offset / sr
                        print(f"进度: {progress:.1f}% | 时间: {current_time:.1f}s/{duration:.1f}s | 包数: {sent_packets} | 速率: {total_bytes/1024/elapsed:.1f} KB/s", end='\r')

                    # Rate limiting for real-time playback
                    # Sleep to maintain ~real-time streaming
                    frame_elapsed = time.time() - frame_start_time
                    sleep_time = frame_duration - frame_elapsed - 0.001  # 1ms buffer
                    if sleep_time > 0:
                        time.sleep(sleep_time)

                except Exception as e:
                    print(f"\n编码错误 (偏移 {offset}): {e}")
                    import traceback
                    traceback.print_exc()
                    continue

            elapsed = time.time() - start_time
            print(f"\n发送完成!")
            print(f"  总包数: {sent_packets}")
            print(f"  总数据: {total_bytes/1024:.1f} KB")
            print(f"  耗时: {elapsed:.2f}秒")
            print(f"  平均速率: {total_bytes/1024/elapsed:.1f} KB/s")

            return True

        except KeyboardInterrupt:
            print("\n\n用户中断发送")
            return False
        except Exception as e:
            print(f"\n流式发送错误: {e}")
            import traceback
            traceback.print_exc()
            return False

    def close(self):
        """关闭连接"""
        if self.socket:
            try:
                self.socket.close()
                print("连接已关闭")
            except Exception as e:
                print(f"关闭连接时出错: {e}")


def main():
    parser = argparse.ArgumentParser(
        description="将音频文件(FLAC/WAV/MP3等)流式传输到TS3AudioBot TCP服务器"
    )
    parser.add_argument(
        "audio_file",
        help="音频文件路径 (支持 FLAC, WAV, MP3, OGG 等格式)"
    )
    parser.add_argument(
        "--host", "-H",
        default="localhost",
        help="TS3AudioBot服务器地址 (默认: localhost)"
    )
    parser.add_argument(
        "--port", "-p",
        type=int,
        default=9001,
        help="TS3AudioBot TCP端口 (默认: 9001)"
    )
    parser.add_argument(
        "--bitrate", "-b",
        type=int,
        default=96000,
        help="Opus编码比特率 (默认: 96000)"
    )

    args = parser.parse_args()

    print("=" * 60)
    print("Audio File to TCP Streamer for TS3AudioBot")
    print("=" * 60)
    print(f"目标: {args.host}:{args.port}")
    print(f"文件: {args.audio_file}")
    print(f"比特率: {args.bitrate} bps")
    print("=" * 60)

    # Check dependencies
    try:
        import opuslib
        print("[OK] opuslib")
    except ImportError:
        print("[ERROR] opuslib not found. Install with: pip install opuslib")
        print("        Also install libopus: apt-get install libopus0 (Linux)")
        sys.exit(1)
    
    try:
        import numpy
        print("[OK] numpy")
    except ImportError:
        print("[ERROR] numpy not found. Install with: pip install numpy")
        sys.exit(1)
    
    has_audio_loader = False
    try:
        import soundfile
        print("[OK] soundfile")
        has_audio_loader = True
    except ImportError:
        print("[--] soundfile not installed (optional)")
    
    try:
        import librosa
        print("[OK] librosa")
        has_audio_loader = True
    except ImportError:
        print("[--] librosa not installed (optional)")
    
    if not has_audio_loader:
        print("[ERROR] Need either soundfile or librosa for audio loading")
        print("        Install with: pip install soundfile")
        print("        Or: pip install librosa")
        sys.exit(1)
    
    print("=" * 60)

    streamer = AudioTcpStreamer(args.host, args.port, args.audio_file)
    streamer.bitrate = args.bitrate

    try:
        if streamer.connect():
            streamer.stream()
        else:
            sys.exit(1)
    finally:
        streamer.close()


if __name__ == "__main__":
    main()
