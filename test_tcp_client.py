#!/usr/bin/env python3
"""
TCP Audio Client for TS3AudioBot
Tests receiving audio streams and voice input from the bot

Enhanced protocol support:
- AudioOutput (type 0): Bot's audio output
- VoiceInput (type 1): Voice from TS users with sender ID
- AudioFromClient (type 2): Send audio to bot
- Command (type 3): Control commands
"""

import socket
import struct
import sys
import time

HOST = 'localhost'
PORT = 9001

# Packet types
PACKET_AUDIO_OUTPUT = 0
PACKET_VOICE_INPUT = 1
PACKET_AUDIO_FROM_CLIENT = 2
PACKET_COMMAND = 3

# Codec types (must match TSLib/TsEnums.cs Codec enum)
# SpeexNarrowband = 0
# SpeexWideband = 1
# SpeexUltraWideband = 2
# CeltMono = 3
# OpusVoice = 4  (mono, 48kHz)
# OpusMusic = 5  (stereo, 48kHz)
CODEC_OPUS_VOICE = 4
CODEC_OPUS_MUSIC = 5

# Command types
CMD_STOP = 0
CMD_CLEAR_QUEUE = 1
CMD_PAUSE = 2
CMD_RESUME = 3


def recv_exact(sock, count):
    """Receive exactly count bytes."""
    data = b''
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            return None
        data += chunk
    return data


def main():
    print(f"Connecting to TS3AudioBot TCP audio server at {HOST}:{PORT}...")
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.connect((HOST, PORT))
        print("Connected successfully!")
        print("Waiting for audio packets... (Press Ctrl+C to exit)")
        print("-" * 70)
        
        audio_output_count = 0
        voice_input_count = 0
        total_bytes = 0
        start_time = time.time()
        speaker_activity = {}  # Track last activity per speaker
        
        while True:
            # Read length header (4 bytes)
            header = recv_exact(sock, 4)
            if not header:
                print("\nConnection closed by server")
                return
            
            length = struct.unpack('<I', header)[0]  # Little-endian int32
            
            # Read packet data
            data = recv_exact(sock, length)
            if not data or len(data) < length:
                print("\nConnection closed while reading data")
                return
            
            packet_type = data[0]
            total_bytes += length
            
            if packet_type == PACKET_AUDIO_OUTPUT:
                # [PacketType:1][Codec:1][AudioData:N]
                codec = data[1]
                audio_data = data[2:]
                audio_output_count += 1
                
                codec_name = {
                    0: "OpusVoice",
                    1: "OpusMusic",
                    2: "Speex",
                    3: "Celt"
                }.get(codec, f"Unknown({codec})")
                
                print(f"[AudioOutput] #{audio_output_count:4d} | Codec: {codec_name:12s} | Size: {len(audio_data):5d} bytes", end='\r')
                
            elif packet_type == PACKET_VOICE_INPUT:
                # [PacketType:1][SenderId:2][Codec:1][AudioData:N]
                sender_id = struct.unpack('<H', data[1:3])[0]
                codec = data[3]
                audio_data = data[4:]
                voice_input_count += 1
                
                codec_name = {
                    0: "OpusVoice",
                    1: "OpusMusic",
                    2: "Speex",
                    3: "Celt"
                }.get(codec, f"Unknown({codec})")
                
                # Track speaker activity
                speaker_activity[sender_id] = time.time()
                active_speakers = [sid for sid, t in speaker_activity.items() if time.time() - t < 1.0]
                
                print(f"[VoiceInput]  #{voice_input_count:4d} | User: {sender_id:5d} | Codec: {codec_name:12s} | Size: {len(audio_data):5d} bytes | Active speakers: {len(active_speakers)}", end='\r')
                
            else:
                print(f"\n[Unknown packet type: {packet_type}]")
            
            sys.stdout.flush()
            
    except ConnectionRefusedError:
        print(f"ERROR: Connection refused. Make sure:")
        print("  1. TS3AudioBot is running")
        print("  2. TCP server is enabled in config (tcp_server.enabled = true)")
        print(f"  3. Port {PORT} is correct")
        sys.exit(1)
    except KeyboardInterrupt:
        elapsed = time.time() - start_time
        print("\n\nDisconnecting...")
        print(f"Summary:")
        print(f"  Audio output packets: {audio_output_count}")
        print(f"  Voice input packets:  {voice_input_count}")
        print(f"  Total bytes:          {total_bytes:,}")
        print(f"  Duration:             {elapsed:.1f}s")
        if elapsed > 0:
            print(f"  Average bitrate:      {(total_bytes * 8) / (elapsed * 1000):.2f} kbps")
    except Exception as e:
        print(f"\nERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
    finally:
        sock.close()
        print("Connection closed.")


if __name__ == '__main__':
    main()
