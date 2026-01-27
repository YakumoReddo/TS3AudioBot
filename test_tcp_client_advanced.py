#!/usr/bin/env python3
"""
Advanced TCP Audio Client for TS3AudioBot
Demonstrates receiving, decoding, and saving audio streams,
as well as sending commands and audio.

Requires: pip install opuslib numpy
"""

import socket
import struct
import sys
import wave
import threading
import time

HOST = 'localhost'
PORT = 9001
SAMPLE_RATE = 48000
CHANNELS = 2
OUTPUT_FILE = 'received_audio.wav'
VOICE_OUTPUT_PREFIX = 'voice_user_'

# Packet types
PACKET_AUDIO_OUTPUT = 0
PACKET_VOICE_INPUT = 1
PACKET_AUDIO_FROM_CLIENT = 2
PACKET_COMMAND = 3

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


def send_command(sock, command_type):
    """Send a control command to the bot."""
    # [Length:4][PacketType:1=3][CommandType:1]
    length = 2
    packet = struct.pack('<I', length) + bytes([PACKET_COMMAND, command_type])
    sock.sendall(packet)


def send_audio(sock, opus_data, codec=1):
    """Send audio data to be played by the bot."""
    # [Length:4][PacketType:1=2][Codec:1][AudioData:N]
    length = 1 + 1 + len(opus_data)
    packet = struct.pack('<I', length) + bytes([PACKET_AUDIO_FROM_CLIENT, codec]) + opus_data
    sock.sendall(packet)


def main():
    print("Advanced TS3AudioBot TCP Audio Client")
    print("=" * 70)
    print("This client will:")
    print("  1. Connect to the bot's TCP audio server")
    print("  2. Receive Opus-encoded audio packets (bot output + user voice)")
    print("  3. Decode them to PCM")
    print("  4. Save bot audio to WAV file")
    print("  5. Track and save individual user voice to separate files")
    print("")
    
    # Try to import opuslib
    try:
        import opuslib
        print("[OK] opuslib is installed")
    except ImportError:
        print("[ERROR] opuslib not found. Install with: pip install opuslib")
        print("        You also need libopus installed on your system:")
        print("        - Linux: apt-get install libopus0")
        print("        - macOS: brew install opus")
        print("        - Windows: Download opus.dll from opus-codec.org")
        sys.exit(1)
    
    print(f"\nConnecting to {HOST}:{PORT}...")
    
    # Initialize variables
    sock = None
    wav_file = None
    voice_wav_files = {}  # sender_id -> wav file
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(1.0)  # Set timeout to allow Ctrl+C to work
        sock.connect((HOST, PORT))
        print("Connected successfully!")
        
        # Create Opus decoders
        music_decoder = opuslib.Decoder(SAMPLE_RATE, 2)  # Stereo for music
        voice_decoders = {}  # sender_id -> decoder (mono)
        print(f"Opus decoders initialized")
        
        # Prepare WAV file for bot audio output
        wav_file = wave.open(OUTPUT_FILE, 'wb')
        wav_file.setnchannels(CHANNELS)
        wav_file.setsampwidth(2)  # 16-bit samples
        wav_file.setframerate(SAMPLE_RATE)
        
        print(f"Recording bot audio to: {OUTPUT_FILE}")
        print("Press Ctrl+C to stop...\n")
        print("Commands: Type 'stop', 'pause', 'resume', 'clear' and press Enter")
        print("-" * 70)
        
        audio_output_count = 0
        voice_input_count = 0
        decoded_frames = 0
        speaker_activity = {}
        
        # Start command input thread
        def command_input():
            while True:
                try:
                    cmd = input().strip().lower()
                    if cmd == 'stop':
                        send_command(sock, CMD_STOP)
                        print("[CMD] Sent stop command")
                    elif cmd == 'pause':
                        send_command(sock, CMD_PAUSE)
                        print("[CMD] Sent pause command")
                    elif cmd == 'resume':
                        send_command(sock, CMD_RESUME)
                        print("[CMD] Sent resume command")
                    elif cmd == 'clear':
                        send_command(sock, CMD_CLEAR_QUEUE)
                        print("[CMD] Sent clear queue command")
                    elif cmd:
                        print(f"[CMD] Unknown command: {cmd}")
                except:
                    break
        
        cmd_thread = threading.Thread(target=command_input, daemon=True)
        cmd_thread.start()
        
        while True:
            try:
                # Read length header
                header = recv_exact(sock, 4)
                if not header:
                    print("\nConnection closed by server")
                    break
                
                length = struct.unpack('<I', header)[0]
                
                # Read packet data
                data = recv_exact(sock, length)
                if not data or len(data) < length:
                    print("\nConnection closed while reading data")
                    break
                
                packet_type = data[0]
                
                if packet_type == PACKET_AUDIO_OUTPUT:
                    # [PacketType:1][Codec:1][AudioData:N]
                    codec = data[1]
                    audio_data = data[2:]
                    audio_output_count += 1
                    
                    # Decode Opus to PCM
                    if codec in [0, 1]:  # OpusVoice or OpusMusic
                        try:
                            pcm_data = music_decoder.decode(audio_data, frame_size=960)
                            wav_file.writeframes(pcm_data)
                            decoded_frames += 1
                        except Exception as e:
                            print(f"\n[ERROR] Decode error: {e}")
                    
                    if audio_output_count % 50 == 0:
                        duration = decoded_frames * 0.02
                        print(f"[Audio] Packets: {audio_output_count:5d} | Duration: {duration:6.2f}s", end='\r')
                        sys.stdout.flush()
                
                elif packet_type == PACKET_VOICE_INPUT:
                    # [PacketType:1][SenderId:2][Codec:1][AudioData:N]
                    sender_id = struct.unpack('<H', data[1:3])[0]
                    codec = data[3]
                    audio_data = data[4:]
                    voice_input_count += 1
                    
                    # Track speaker activity
                    speaker_activity[sender_id] = time.time()
                    active_speakers = [sid for sid, t in speaker_activity.items() if time.time() - t < 1.0]
                    
                    # Create decoder and wav file for this speaker if needed
                    if sender_id not in voice_decoders:
                        voice_decoders[sender_id] = opuslib.Decoder(SAMPLE_RATE, 1)  # Mono for voice
                        voice_file_path = f"{VOICE_OUTPUT_PREFIX}{sender_id}.wav"
                        voice_wav = wave.open(voice_file_path, 'wb')
                        voice_wav.setnchannels(1)  # Mono
                        voice_wav.setsampwidth(2)
                        voice_wav.setframerate(SAMPLE_RATE)
                        voice_wav_files[sender_id] = voice_wav
                        print(f"\n[NEW] Recording voice from user {sender_id} to: {voice_file_path}")
                    
                    # Decode and save voice
                    if codec == 0:  # OpusVoice (mono)
                        try:
                            pcm_data = voice_decoders[sender_id].decode(audio_data, frame_size=960)
                            voice_wav_files[sender_id].writeframes(pcm_data)
                        except Exception as e:
                            print(f"\n[ERROR] Voice decode error: {e}")
                    
                    if voice_input_count % 25 == 0:
                        print(f"[Voice] Packets: {voice_input_count:5d} | Active speakers: {len(active_speakers)} | IDs: {active_speakers}", end='\r')
                        sys.stdout.flush()
                        
            except socket.timeout:
                continue
                    
    except ConnectionRefusedError:
        print(f"ERROR: Connection refused.")
        print("Make sure:")
        print("  1. TS3AudioBot is running and connected to TeamSpeak")
        print("  2. TCP server is enabled: tcp_server.enabled = true")
        sys.exit(1)
        
    except KeyboardInterrupt:
        print("\n\nStopping...")
        
    except Exception as e:
        print(f"\nERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
        
    finally:
        # Safe cleanup
        if sock is not None:
            try:
                sock.close()
                print("Socket closed.")
            except:
                pass
        
        if wav_file is not None:
            try:
                wav_file.close()
                print("Bot audio WAV file closed.")
            except:
                pass
        
        for sender_id, voice_wav in voice_wav_files.items():
            try:
                voice_wav.close()
                print(f"Voice WAV file for user {sender_id} closed.")
            except:
                pass
        
        # Summary
        if audio_output_count > 0 or voice_input_count > 0:
            duration = decoded_frames * 0.02
            print(f"\nSummary:")
            print(f"  Audio output packets: {audio_output_count}")
            print(f"  Voice input packets:  {voice_input_count}")
            print(f"  Decoded frames:       {decoded_frames}")
            print(f"  Bot audio duration:   {duration:.2f} seconds")
            print(f"  Saved to:             {OUTPUT_FILE}")
            if voice_wav_files:
                print(f"  Voice files:          {list(voice_wav_files.keys())}")
        else:
            print("\nNo audio data received.")


if __name__ == '__main__':
    main()
