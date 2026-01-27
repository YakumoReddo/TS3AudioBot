# TCP Audio Streaming Feature

## Overview
The TCP audio streaming feature allows external applications (Python scripts, AI processors, etc.) to connect to TS3AudioBot via TCP and:

1. **Receive bot audio output** - What the bot is currently playing
2. **Receive voice input from TS users** - Real-time voice from users speaking in TeamSpeak
3. **Send audio to the bot** - Stream audio that the bot will play in TeamSpeak
4. **Control playback** - Stop, pause, resume, and clear audio queue (interrupt functionality)

This enables powerful AI integration scenarios:
- **Speech-to-text**: Process user voice with Whisper
- **LLM integration**: Send transcribed text to ChatGPT/LLM for processing
- **Text-to-speech**: Generate AI responses and play them back
- **Smart interruption**: Stop current audio when user gives new commands

## Quick Start

### 1. Enable TCP Server
Add to your bot configuration file (`bots/<botname>.toml`):

```toml
[audio.tcp_server]
enabled = true
port = 9001
send_audio = true
receive_audio = true
```

### 2. Test Connection
Run the simple test client:

```bash
python3 test_tcp_client.py
```

This will connect to the bot and display received audio packets (both bot output and user voice).

### 3. Advanced Usage
For decoding, saving audio, and sending commands:

```bash
# Install dependencies
pip install opuslib

# Run advanced client
python3 test_tcp_client_advanced.py
```

This will:
- Decode Opus audio and save bot output to `received_audio.wav`
- Save individual user voice to separate files (`voice_user_<id>.wav`)
- Accept commands: `stop`, `pause`, `resume`, `clear`

## AI Integration Workflow

The typical AI assistant workflow:

```
User speaks in TS → Bot receives VoiceInput packets → Forward to Python
→ Python decodes Opus → Whisper transcription → LLM processing
→ Generate TTS response → Encode to Opus → Send AudioFromClient packets
→ Bot plays response in TS
```

### Interrupt Capability
When the user speaks while audio is playing:
1. Python receives new VoiceInput packets
2. Whisper transcribes the speech
3. LLM detects interrupt intent (e.g., "stop", "quiet", new command)
4. Python sends Command packet (StopPlayback)
5. Bot immediately stops current audio
6. Process and respond to new command

## Protocol Summary

| Packet Type | Direction | Description |
|-------------|-----------|-------------|
| AudioOutput (0) | Bot → Client | What the bot is playing |
| VoiceInput (1) | Bot → Client | User voice from TeamSpeak (with sender ID) |
| AudioFromClient (2) | Client → Bot | Audio for bot to play |
| Command (3) | Client → Bot | Control commands (stop/pause/resume/clear) |

### Multi-User Voice Handling
When multiple users speak simultaneously:
- Each user's voice is sent as separate packets
- Packets include `SenderId` to identify the speaker
- Your client should track packets per-user for accurate transcription

## Files
- `TcpAudioServer.cs` - Server implementation
- `VoiceInputForwarder.cs` - Captures voice from TS users
- `TCP_AUDIO_PROTOCOL.md` - Complete protocol specification
- `test_tcp_client.py` - Simple test client
- `test_tcp_client_advanced.py` - Advanced client with decoding and commands

## Example: Python AI Assistant

```python
import socket
import struct

HOST = 'localhost'
PORT = 9001

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect((HOST, PORT))

def handle_voice(sender_id, audio_data):
    # Decode with Opus, transcribe with Whisper
    text = whisper_transcribe(audio_data)
    
    # Check for interrupt
    if is_interrupt_command(text):
        send_stop_command()
        return
    
    # Process with LLM
    response = llm_process(text)
    
    # Generate TTS and send back
    tts_audio = generate_tts(response)
    send_audio(tts_audio)

def send_stop_command():
    # [Length:4][PacketType:1=3][CommandType:1=0]
    packet = struct.pack('<I', 2) + bytes([3, 0])
    sock.sendall(packet)
```

See [TCP_AUDIO_PROTOCOL.md](TCP_AUDIO_PROTOCOL.md) for complete protocol specification.

## Technical Notes
- Audio format: Opus-encoded at 48kHz
- Bot output: Stereo (2 channels)
- User voice: Typically Mono (1 channel)
- Multiple TCP clients can connect simultaneously
- Server starts/stops with bot's TeamSpeak connection
- Thread-safe client management

## Troubleshooting

**Connection refused:**
- Ensure bot is running and connected to TeamSpeak
- Check `tcp_server.enabled = true` in config
- Verify correct port number

**No audio received:**
- Ensure bot is playing audio or users are speaking
- Check `tcp_server.send_audio = true`

**No voice input:**
- Ensure users are speaking in TeamSpeak
- Bot must be able to hear users (same channel or whisper)

**Decode errors:**
- Install opuslib: `pip install opuslib`
- Install system Opus library
- Use 48kHz decoder settings
- Use stereo (2 channels) for bot output, mono (1 channel) for voice
