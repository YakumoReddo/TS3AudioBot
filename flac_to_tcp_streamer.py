#!/usr/bin/env python3
"""
FLAC to TCP Streamer for TS3AudioBot

读取FLAC文件，编码为Opus，通过TCP发送到TS3AudioBot服务器进行播放。
"""

import socket
import struct
import opuslib
import librosa
import numpy as np
import sys
import time
import argparse


class FlacTcpStreamer:
    def __init__(self, host: str, port: int, flac_path: str):
        self.host = host
        self.port = port
        self.flac_path = flac_path
        self.socket = None
        self.encoder = None
        self.sample_rate = 48000
        self.channels = 2
        self.frame_size = 960
        self.bitrate = 48000

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

    def init_encoder(self):
        """初始化Opus编码器"""
        self.encoder = opuslib.Encoder(
            self.sample_rate,
            self.channels,
            opuslib.APPLICATION_AUDIO
        )
        self.encoder.bitrate = self.bitrate
        print(f"Opus编码器初始化完成 - 采样率: {self.sample_rate}Hz, 声道: {self.channels}")

    def send_packet(self, audio_data: bytes, codec: int = 1):
        """发送音频包
        协议格式: [Length:4字节 (小端int32)][Codec:1字节][Audio Data]
        """
        if not self.socket:
            raise RuntimeError("未建立TCP连接")
        packet = struct.pack('<I', len(audio_data)) + bytes([codec]) + audio_data
        self.socket.sendall(packet)

    def stream(self) -> bool:
        """流式发送FLAC文件"""
        try:
            print(f"正在加载FLAC文件: {self.flac_path}")
            audio, sr = librosa.load(
                self.flac_path,
                sr=self.sample_rate,
                mono=False,
                dtype=np.float32
            )
            print(f"音频加载完成 - 采样率: {sr}Hz, 形状: {audio.shape}, 时长: {len(audio)/sr:.2f}秒")

            if audio.ndim == 1:
                audio = np.tile(audio[:, np.newaxis], (1, self.channels))
            elif audio.shape[0] == 1:
                audio = np.repeat(audio, self.channels, axis=0)
            elif audio.shape[0] != self.channels:
                if audio.shape[1] == self.channels:
                    audio = audio.T
                else:
                    print(f"错误: 无法处理的音频形状 {audio.shape}")
                    return False

            self.init_encoder()

            total_samples = audio.shape[1]
            sent_packets = 0
            total_bytes = 0
            start_time = time.time()

            print("开始流式发送音频...")

            for offset in range(0, total_samples, self.frame_size):
                frame = audio[:, offset:offset + self.frame_size]

                if frame.shape[1] < self.frame_size:
                    pad_width = ((0, 0), (0, self.frame_size - frame.shape[1]))
                    frame = np.pad(frame, pad_width, mode='constant', constant_values=0)

                try:
                    frame_int16 = (frame * 32767).astype(np.int16)
                    opus_data = self.encoder.encode(frame_int16.tobytes(), self.frame_size)
                    self.send_packet(opus_data, codec=5)

                    sent_packets += 1
                    total_bytes += len(opus_data)

                    if sent_packets % 100 == 0:
                        elapsed = time.time() - start_time
                        progress = (offset + self.frame_size) / total_samples * 100
                        print(f"进度: {progress:.1f}% | 包数: {sent_packets} | 速率: {total_bytes/1024/elapsed:.1f} KB/s")

                except opuslib.OpusError as e:
                    print(f"编码错误 (偏移 {offset}): {e}")
                    continue

            elapsed = time.time() - start_time
            print(f"发送完成!")
            print(f"  总包数: {sent_packets}")
            print(f"  总数据: {total_bytes/1024:.1f} KB")
            print(f"  耗时: {elapsed:.2f}秒")
            print(f"  平均速率: {total_bytes/1024/elapsed:.1f} KB/s")

            return True

        except Exception as e:
            print(f"流式发送错误: {e}")
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
        description="将FLAC文件流式传输到TS3AudioBot TCP服务器"
    )
    parser.add_argument(
        "flac_file",
        help="FLAC文件路径"
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

    args = parser.parse_args()

    print("=" * 50)
    print("FLAC to TCP Streamer for TS3AudioBot")
    print("=" * 50)
    print(f"目标: {args.host}:{args.port}")
    print(f"文件: {args.flac_file}")
    print("=" * 50)

    streamer = FlacTcpStreamer(args.host, args.port, args.flac_file)

    try:
        if streamer.connect():
            streamer.stream()
        else:
            sys.exit(1)
    finally:
        streamer.close()


if __name__ == "__main__":
    main()
