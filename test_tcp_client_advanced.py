#!/usr/bin/env python3
"""
Advanced TCP Audio Client for TS3AudioBot
Demonstrates receiving, decoding, and saving audio streams
Requires: pip install opuslib numpy
"""

import socket
import struct
import sys
import wave
import numpy as np

HOST = 'localhost'
PORT = 9001
SAMPLE_RATE = 48000
CHANNELS = 2
OUTPUT_FILE = 'received_audio.wav'

def main():
    print("Advanced TS3AudioBot TCP Audio Client")
    print("=" * 60)
    print("This client will:")
    print("  1. Connect to the bot's TCP audio server")
    print("  2. Receive Opus-encoded audio packets")
    print("  3. Decode them to PCM")
    print("  4. Save to WAV file")
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
    
    # Initialize variables to ensure they exist in finally block
    sock = None
    wav_file = None
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(1.0)  # Set timeout to allow Ctrl+C to work
        sock.connect((HOST, PORT))
        print("Connected successfully!")
        
        # Create Opus decoder
        decoder = opuslib.Decoder(SAMPLE_RATE, CHANNELS)
        print(f"Opus decoder initialized (48kHz, stereo)")
        
        # Prepare WAV file for output
        wav_file = wave.open(OUTPUT_FILE, 'wb')
        wav_file.setnchannels(CHANNELS)
        wav_file.setsampwidth(2)  # 16-bit samples
        wav_file.setframerate(SAMPLE_RATE)
        
        print(f"Recording to: {OUTPUT_FILE}")
        print("Press Ctrl+C to stop...\n")
        
        packet_count = 0
        decoded_frames = 0
        
        while True:
            try:
                # Read packet header with timeout
                header = b''
                while len(header) < 5:
                    chunk = sock.recv(5 - len(header))
                    if not chunk:
                        print("\nConnection closed by server")
                        return  # Exit cleanly when connection closes
                    header += chunk
                
                if len(header) < 5:
                    break
                
                length = struct.unpack('<I', header[:4])[0]
                codec = header[4]
                
                # Read audio data
                audio_data = b''
                remaining = length
                while remaining > 0:
                    chunk = sock.recv(min(remaining, 4096))
                    if not chunk:
                        print("\nConnection closed while reading data")
                        return  # Exit cleanly when connection closes
                    audio_data += chunk
                    remaining -= len(chunk)
                
                if len(audio_data) < length:
                    break
                
                packet_count += 1
                
                # Decode Opus to PCM if codec is Opus
                if codec in [0, 1]:  # OpusVoice or OpusMusic
                    try:
                        # Decode with frame size 960 (20ms at 48kHz)
                        pcm_data = decoder.decode(audio_data, frame_size=960)
                        
                        # Write to WAV file
                        wav_file.writeframes(pcm_data)
                        decoded_frames += 1
                        
                        if packet_count % 50 == 0:  # Update every 50 packets (~1 second)
                            duration = decoded_frames * 0.02  # 20ms per frame
                            print(f"Packets: {packet_count:5d} | Duration: {duration:6.2f}s | Size: {len(audio_data):5d} bytes", end='\r')
                            sys.stdout.flush()
                            
                    except Exception as e:
                        print(f"\nDecode error: {e}")
                        
                else:
                    print(f"\nWarning: Unsupported codec {codec}, skipping packet")
                    
            except socket.timeout:
                # Timeout is normal, just continue the loop
                # This allows Ctrl+C to be processed
                continue
                    
    except ConnectionRefusedError:
        print(f"ERROR: Connection refused.")
        print("Make sure:")
        print("  1. TS3AudioBot is running and connected to TeamSpeak")
        print("  2. TCP server is enabled: tcp_server.enabled = true")
        print("  3. Bot is playing audio")
        sys.exit(1)
        
    except KeyboardInterrupt:
        print("\n\nStopping...")
        
    except Exception as e:
        print(f"\nERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
        
    finally:
        # Safe cleanup with null checks
        if sock is not None:
            try:
                sock.close()
                print("Socket closed.")
            except:
                pass
        
        if wav_file is not None:
            try:
                wav_file.close()
                print("WAV file closed.")
            except:
                pass
        
        if packet_count > 0:  # Only show summary if we received some data
            duration = decoded_frames * 0.02
            print(f"\nSummary:")
            print(f"  Packets received: {packet_count}")
            print(f"  Frames decoded: {decoded_frames}")
            print(f"  Duration: {duration:.2f} seconds")
            print(f"  Saved to: {OUTPUT_FILE}")
        else:
            print("\nNo audio data received.")

if __name__ == '__main__':
    main()
