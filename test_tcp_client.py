#!/usr/bin/env python3
"""
Simple TCP Audio Client for TS3AudioBot
Tests receiving audio stream from the bot
"""

import socket
import struct
import sys
import time

HOST = 'localhost'
PORT = 9001

def main():
    print(f"Connecting to TS3AudioBot TCP audio server at {HOST}:{PORT}...")
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.connect((HOST, PORT))
        print("Connected successfully!")
        print("Waiting for audio packets... (Press Ctrl+C to exit)")
        print("-" * 60)
        
        packet_count = 0
        total_bytes = 0
        start_time = time.time()
        
        while True:
            # Read packet header (4 bytes length + 1 byte codec)
            header = b''
            while len(header) < 5:
                chunk = sock.recv(5 - len(header))
                if not chunk:
                    print("\nConnection closed by server")
                    return
                header += chunk
            
            length = struct.unpack('<I', header[:4])[0]  # Little-endian int32
            codec = header[4]
            
            # Read audio data
            audio_data = b''
            remaining = length
            while remaining > 0:
                chunk = sock.recv(min(remaining, 4096))
                if not chunk:
                    print("\nConnection closed while reading data")
                    return
                audio_data += chunk
                remaining -= len(chunk)
            
            packet_count += 1
            total_bytes += length
            
            codec_name = {
                0: "OpusVoice",
                1: "OpusMusic",
                2: "Speex",
                3: "Celt"
            }.get(codec, f"Unknown({codec})")
            
            elapsed = time.time() - start_time
            if elapsed > 0:
                kbps = (total_bytes * 8) / (elapsed * 1000)
            else:
                kbps = 0
            
            print(f"Packet #{packet_count:4d} | Codec: {codec_name:12s} | Size: {length:5d} bytes | Rate: {kbps:6.2f} kbps", end='\r')
            sys.stdout.flush()
            
    except ConnectionRefusedError:
        print(f"ERROR: Connection refused. Make sure:")
        print("  1. TS3AudioBot is running")
        print("  2. TCP server is enabled in config (tcp_server.enabled = true)")
        print(f"  3. Port {PORT} is correct")
        sys.exit(1)
    except KeyboardInterrupt:
        print("\n\nDisconnecting...")
        print(f"Summary: Received {packet_count} packets, {total_bytes:,} bytes total")
    except Exception as e:
        print(f"\nERROR: {e}")
        sys.exit(1)
    finally:
        sock.close()
        print("Connection closed.")

if __name__ == '__main__':
    main()
