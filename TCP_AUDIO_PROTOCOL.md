# TCP Audio Streaming Protocol Documentation

## Overview
TS3AudioBot supports a TCP-based audio streaming service that allows external clients (e.g., Python scripts, AI processors) to:
1. **Receive bot audio output** - What the bot is currently playing (music, TTS, etc.)
2. **Receive voice input from TS users** - Audio from users speaking in TeamSpeak
3. **Send audio to the bot** - Stream audio that the bot will play in TeamSpeak
4. **Control playback** - Stop, pause, resume, and clear audio queue

This enables powerful AI integration scenarios like:
- Real-time speech-to-text using Whisper
- LLM-based command processing
- Text-to-speech response generation
- Smart music/content interruption

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

### Packet Types
| Type | Value | Description |
|------|-------|-------------|
| AudioOutput | 0 | Bot's audio output (music, TTS being played) |
| VoiceInput | 1 | Voice from TeamSpeak users speaking |
| AudioFromClient | 2 | Audio sent by TCP client to play on TS |
| Command | 3 | Control commands (stop, pause, etc.) |

### Packet Structures

#### 1. Audio Output (Bot -> Client)
What the bot is currently playing (music, TTS, etc.).

```
[Length: 4 bytes (int32, LE)] [PacketType: 1 byte = 0] [Codec: 1 byte] [Audio Data: N bytes]
```

- **Length**: Total length after this field (PacketType + Codec + AudioData)
- **PacketType**: 0 (AudioOutput)
- **Codec**: See Codec Types below
- **Audio Data**: Opus-encoded audio frames

#### 2. Voice Input (Bot -> Client)
Voice from users speaking in TeamSpeak. Includes sender identification.

```
[Length: 4 bytes (int32, LE)] [PacketType: 1 byte = 1] [SenderId: 2 bytes (uint16, LE)] [Codec: 1 byte] [Audio Data: N bytes]
```

- **Length**: Total length after this field
- **PacketType**: 1 (VoiceInput)
- **SenderId**: TeamSpeak ClientId of the speaker (allows identifying who is speaking)
- **Codec**: See Codec Types below
- **Audio Data**: Opus-encoded audio frames

**Important**: When multiple users speak simultaneously, you receive **separate packets for each user**. Each packet contains a single user's voice with their unique SenderId. Your client should:
- Track packets by SenderId to handle multiple simultaneous speakers
- Potentially mix audio from multiple users if needed
- Use SenderId to associate voice with specific users

#### 3. Audio From Client (Client -> Bot)
Audio data to be played by the bot in TeamSpeak.

```
[Length: 4 bytes (int32, LE)] [PacketType: 1 byte = 2] [Codec: 1 byte] [Audio Data: N bytes]
```

- **Length**: Total length after this field
- **PacketType**: 2 (AudioFromClient)
- **Codec**: See Codec Types below
- **Audio Data**: Opus-encoded audio frames

#### 4. Command (Client -> Bot)
Control commands for playback.

```
[Length: 4 bytes (int32, LE)] [PacketType: 1 byte = 3] [CommandType: 1 byte] [CommandData: optional]
```

Command Types:
| CommandType | Value | Description |
|-------------|-------|-------------|
| StopPlayback | 0 | Stop current playback immediately |
| ClearQueue | 1 | Clear pending audio in the queue |
| Pause | 2 | Pause playback |
| Resume | 3 | Resume playback |

### Codec Types
- `0` = OpusVoice (Opus codec, voice quality, mono)
- `1` = OpusMusic (Opus codec, music quality, stereo) - **Default for bot output**
- `2` = Speex (legacy)
- `3` = Celt (legacy)

## Audio Specifications
**Bot Output (AudioOutput)**:
- **Codec**: Opus (OpusMusic)
- **Sample Rate**: 48,000 Hz
- **Channels**: 2 (Stereo)
- **Bitrate**: Configurable (default 48 kbps)
- **Format**: Pre-encoded Opus frames

**User Voice (VoiceInput)**:
- **Codec**: Typically OpusVoice (mono) or OpusMusic (stereo)
- **Sample Rate**: 48,000 Hz
- **Channels**: 1 (Mono) for voice, 2 (Stereo) for music codec
- **Format**: Pre-encoded Opus frames

## Python Client Examples

### Complete AI Integration Example
```python
import socket
import struct
import threading
import queue

HOST = 'localhost'
PORT = 9001

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

class TS3AudioClient:
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.connect((host, port))
        self.voice_queues = {}  # SenderId -> queue of audio packets
        self.running = True
        
    def receive_packets(self, callback):
        """Receive and process packets from the bot."""
        while self.running:
            # Read header
            header = self._recv_exact(4)
            if not header:
                break
            length = struct.unpack('<I', header)[0]
            
            # Read packet data
            data = self._recv_exact(length)
            if not data:
                break
                
            packet_type = data[0]
            
            if packet_type == PACKET_AUDIO_OUTPUT:
                # Bot audio output: [PacketType:1][Codec:1][AudioData:N]
                codec = data[1]
                audio_data = data[2:]
                callback('audio_output', {'codec': codec, 'data': audio_data})
                
            elif packet_type == PACKET_VOICE_INPUT:
                # Voice input: [PacketType:1][SenderId:2][Codec:1][AudioData:N]
                sender_id = struct.unpack('<H', data[1:3])[0]
                codec = data[3]
                audio_data = data[4:]
                callback('voice_input', {
                    'sender_id': sender_id,
                    'codec': codec,
                    'data': audio_data
                })
    
    def send_audio(self, opus_data, codec=1):
        """Send audio to be played by the bot."""
        # [Length:4][PacketType:1=2][Codec:1][AudioData:N]
        length = 1 + 1 + len(opus_data)
        packet = struct.pack('<I', length) + bytes([PACKET_AUDIO_FROM_CLIENT, codec]) + opus_data
        self.sock.sendall(packet)
    
    def send_command(self, command_type):
        """Send a control command."""
        # [Length:4][PacketType:1=3][CommandType:1]
        length = 2
        packet = struct.pack('<I', length) + bytes([PACKET_COMMAND, command_type])
        self.sock.sendall(packet)
    
    def stop_playback(self):
        """Stop current playback (interrupt)."""
        self.send_command(CMD_STOP)
    
    def pause(self):
        """Pause playback."""
        self.send_command(CMD_PAUSE)
    
    def resume(self):
        """Resume playback."""
        self.send_command(CMD_RESUME)
    
    def clear_queue(self):
        """Clear pending audio queue."""
        self.send_command(CMD_CLEAR_QUEUE)
    
    def _recv_exact(self, count):
        """Receive exactly count bytes."""
        data = b''
        while len(data) < count:
            chunk = self.sock.recv(count - len(data))
            if not chunk:
                return None
            data += chunk
        return data
    
    def close(self):
        self.running = False
        self.sock.close()


# Example: AI Voice Assistant Integration
def main():
    client = TS3AudioClient(HOST, PORT)
    
    # Track voice data per speaker
    speaker_buffers = {}
    
    def handle_packet(packet_type, data):
        if packet_type == 'voice_input':
            sender_id = data['sender_id']
            audio_data = data['data']
            
            # Buffer audio for each speaker
            if sender_id not in speaker_buffers:
                speaker_buffers[sender_id] = []
            speaker_buffers[sender_id].append(audio_data)
            
            # Process when we have enough audio (~1 second)
            if len(speaker_buffers[sender_id]) >= 50:  # ~1s at 20ms frames
                process_voice(sender_id, speaker_buffers[sender_id])
                speaker_buffers[sender_id] = []
    
    def process_voice(sender_id, audio_packets):
        # Decode Opus and send to Whisper
        # ... your AI processing here ...
        print(f"Processing voice from user {sender_id}: {len(audio_packets)} packets")
        
        # If AI decides to respond, generate TTS and send
        # response_audio = generate_tts("Hello!")
        # client.send_audio(response_audio)
        
        # If AI decides to interrupt current playback
        # client.stop_playback()
    
    # Start receiving in background thread
    receiver_thread = threading.Thread(
        target=client.receive_packets,
        args=(handle_packet,)
    )
    receiver_thread.daemon = True
    receiver_thread.start()
    
    try:
        print("AI Assistant running... Press Ctrl+C to exit")
        while True:
            import time
            time.sleep(1)
    except KeyboardInterrupt:
        print("Shutting down...")
    finally:
        client.close()

if __name__ == '__main__':
    main()
```

### Simple Voice Input Receiver
```python
import socket
import struct

HOST = 'localhost'
PORT = 9001

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect((HOST, PORT))

print(f"Connected to TS3AudioBot at {HOST}:{PORT}")
print("Listening for voice input from TeamSpeak users...")

try:
    while True:
        # Read length
        header = sock.recv(4)
        if len(header) < 4:
            break
        length = struct.unpack('<I', header)[0]
        
        # Read packet data
        data = b''
        while len(data) < length:
            chunk = sock.recv(length - len(data))
            if not chunk:
                break
            data += chunk
        
        packet_type = data[0]
        
        if packet_type == 1:  # VoiceInput
            sender_id = struct.unpack('<H', data[1:3])[0]
            codec = data[3]
            audio_data = data[4:]
            print(f"Voice from user {sender_id}: {len(audio_data)} bytes (codec: {codec})")
            
except KeyboardInterrupt:
    print("Disconnecting...")
finally:
    sock.close()
```

### Sending Stop/Interrupt Command
```python
import socket
import struct

HOST = 'localhost'
PORT = 9001

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect((HOST, PORT))

# Send stop command
# [Length:4][PacketType:1=3][CommandType:1=0]
packet = struct.pack('<I', 2) + bytes([3, 0])  # Length=2, PacketType=3 (Command), CommandType=0 (Stop)
sock.sendall(packet)

print("Stop command sent!")
sock.close()
```

## Multi-User Voice Handling

When multiple users speak simultaneously in TeamSpeak, the bot receives and forwards **separate voice packets for each user**. Each packet includes:
- **SenderId**: The unique TeamSpeak ClientId identifying the speaker

Your Python client should:
1. **Track packets by SenderId** to keep voice data separate per user
2. **Buffer audio per user** for speech recognition processing
3. **Mix audio if needed** for scenarios where you need combined audio

Example handling multiple speakers:
```python
speaker_buffers = {}  # SenderId -> list of audio packets

def handle_voice_packet(sender_id, audio_data):
    if sender_id not in speaker_buffers:
        speaker_buffers[sender_id] = []
    speaker_buffers[sender_id].append(audio_data)
    
    # Process each speaker independently
    if len(speaker_buffers[sender_id]) >= 50:  # ~1 second
        process_speaker(sender_id, speaker_buffers[sender_id])
        speaker_buffers[sender_id] = []
```

## Notes
- The TCP server starts when the bot connects to TeamSpeak
- The TCP server stops when the bot disconnects from TeamSpeak
- Multiple clients can connect simultaneously
- Audio is broadcast to all connected clients
- Voice input from TS users is broadcast to all TCP clients with sender identification

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
- **No audio received**: Check that `tcp_server.send_audio = true` and someone is speaking/playing
- **No voice input**: Ensure users are speaking in TeamSpeak and the bot can hear them
- **Decode errors**: Verify you're using the correct Opus decoder settings (48kHz)
