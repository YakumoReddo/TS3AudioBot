# TCP Audio Streaming Protocol Documentation

## Overview
TS3AudioBot now supports a TCP-based audio streaming service that allows external clients (e.g., Python scripts, AI processors) to receive and send audio streams.

## Configuration
Enable the TCP audio server by adding the following to your bot configuration file (`bots/<botname>.toml`):

```toml
[audio.tcp_server]
enabled = true
port = 9001
send_audio = true
receive_audio = true
```

Configuration options:
- `enabled`: Enable/disable the TCP audio server (default: false)
- `port`: TCP port number to listen on (default: 9001)
- `send_audio`: Allow streaming audio to connected clients (default: true)
- `receive_audio`: Allow receiving audio from connected clients (default: true)

## Protocol Format

### Packet Structure
All packets follow this binary format:

```
[Length: 4 bytes (int32, little-endian)] [Codec: 1 byte] [Audio Data: N bytes]
```

- **Length**: 4-byte integer representing the length of audio data (not including header)
- **Codec**: 1-byte codec identifier (see Codec Types below)
- **Audio Data**: Raw encoded audio data

### Codec Types
- `0` = OpusVoice (Opus codec, voice quality)
- `1` = OpusMusic (Opus codec, music quality) - **Default for bot output**
- `2` = Speex (legacy)
- `3` = Celt (legacy)

Most modern use cases should use OpusMusic (codec byte = `1`).

## Audio Specifications
When the bot sends audio:
- **Codec**: Opus (OpusMusic)
- **Sample Rate**: 48,000 Hz
- **Channels**: 2 (Stereo)
- **Bitrate**: Configurable (default 48 kbps)
- **Format**: Pre-encoded Opus frames

## Python Client Example

### Receiving Audio Stream
```python
import socket
import struct

HOST = 'localhost'
PORT = 9001

# Connect to the bot
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect((HOST, PORT))

print(f"Connected to TS3AudioBot TCP server at {HOST}:{PORT}")

try:
    while True:
        # Read packet header (4 bytes length + 1 byte codec)
        header = sock.recv(5)
        if len(header) < 5:
            break
        
        length = struct.unpack('<I', header[:4])[0]  # Little-endian int32
        codec = header[4]
        
        # Read audio data
        audio_data = b''
        remaining = length
        while remaining > 0:
            chunk = sock.recv(min(remaining, 4096))
            if not chunk:
                break
            audio_data += chunk
            remaining -= len(chunk)
        
        print(f"Received packet: codec={codec}, length={length}")
        
        # Process audio_data here
        # For Opus codec (1), you'll need to decode it using an Opus decoder
        # Example with opuslib:
        # import opuslib
        # decoder = opuslib.Decoder(48000, 2)
        # pcm_data = decoder.decode(audio_data, frame_size=960)
        
except KeyboardInterrupt:
    print("Disconnecting...")
finally:
    sock.close()
```

### Sending Audio Stream
```python
import socket
import struct

HOST = 'localhost'
PORT = 9001

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect((HOST, PORT))

# Encode your audio to Opus first
# import opuslib
# encoder = opuslib.Encoder(48000, 2, opuslib.APPLICATION_AUDIO)
# opus_data = encoder.encode(pcm_samples, frame_size=960)

# Example: send encoded Opus data
opus_data = b'...'  # Your Opus-encoded audio data
codec = 1  # OpusMusic

# Build packet
packet = struct.pack('<I', len(opus_data)) + bytes([codec]) + opus_data

# Send to bot
sock.sendall(packet)

sock.close()
```

## Advanced Usage with AI Processing

### Example: Speech-to-Text Processing
```python
import socket
import struct
import opuslib
from your_ai_library import speech_to_text

decoder = opuslib.Decoder(48000, 2)
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect(('localhost', 9001))

pcm_buffer = []

while True:
    header = sock.recv(5)
    if len(header) < 5:
        break
    
    length, codec = struct.unpack('<IB', header)
    audio_data = sock.recv(length)
    
    # Decode Opus to PCM
    pcm_samples = decoder.decode(audio_data, frame_size=960)
    pcm_buffer.append(pcm_samples)
    
    # Process every 3 seconds of audio
    if len(pcm_buffer) >= 150:  # ~3 seconds at 20ms frames
        full_audio = b''.join(pcm_buffer)
        text = speech_to_text(full_audio)
        print(f"Transcribed: {text}")
        pcm_buffer.clear()

sock.close()
```

## Notes
- The TCP server starts when the bot connects to TeamSpeak
- The TCP server stops when the bot disconnects from TeamSpeak
- Multiple clients can connect simultaneously
- Audio is broadcasted to all connected clients
- Incoming audio from clients is currently logged but not yet injected into the bot's audio pipeline (future enhancement)

## Dependencies
For Python clients working with Opus codec:
```bash
pip install opuslib
```

You'll also need the Opus library installed on your system:
- **Linux**: `apt-get install libopus0` or `yum install opus`
- **Windows**: Download opus.dll from opus-codec.org
- **macOS**: `brew install opus`

## Troubleshooting
- **Connection refused**: Ensure the bot is connected and `tcp_server.enabled = true` in config
- **No audio received**: Check that `tcp_server.send_audio = true` and the bot is playing audio
- **Decode errors**: Verify you're using the correct Opus decoder settings (48kHz, stereo)
