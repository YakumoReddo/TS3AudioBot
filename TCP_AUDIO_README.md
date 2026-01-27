# TCP Audio Streaming Feature

## Overview
The TCP audio streaming feature allows external applications (Python scripts, AI processors, etc.) to connect to TS3AudioBot via TCP and receive/send audio streams in real-time.

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

This will connect to the bot and display received audio packets.

### 3. Advanced Usage
For decoding and saving audio:

```bash
# Install dependencies
pip install opuslib

# Run advanced client
python3 test_tcp_client_advanced.py
```

This will decode Opus audio and save it to `received_audio.wav`.

## Use Cases

### 1. AI Speech Processing
Connect your Python AI processor to receive audio, transcribe speech, process commands, and send synthesized responses back to TeamSpeak.

```python
import speech_recognition
import text_to_speech

# Receive audio from bot
audio_stream = receive_from_tcp()

# Process with AI
text = speech_recognition.transcribe(audio_stream)
response = ai_process(text)

# Send response back
tts_audio = text_to_speech.synthesize(response)
send_to_tcp(tts_audio)
```

### 2. Audio Recording
Record all audio playing through the bot for archival or analysis.

### 3. Audio Analysis
Perform real-time audio analysis (volume detection, music recognition, etc.).

### 4. Audio Forwarding
Forward audio to multiple destinations simultaneously.

## Protocol Details
See [TCP_AUDIO_PROTOCOL.md](TCP_AUDIO_PROTOCOL.md) for complete protocol specification.

## Files
- `TcpAudioServer.cs` - Server implementation
- `TCP_AUDIO_PROTOCOL.md` - Protocol documentation
- `test_tcp_client.py` - Simple test client
- `test_tcp_client_advanced.py` - Advanced client with Opus decoding

## Technical Notes
- Audio format: Opus-encoded at 48kHz stereo
- Multiple clients can connect simultaneously
- Server starts when bot connects to TeamSpeak
- Server stops when bot disconnects from TeamSpeak
- Thread-safe client management

## Troubleshooting

**Connection refused:**
- Ensure bot is running and connected to TeamSpeak
- Check `tcp_server.enabled = true` in config
- Verify correct port number

**No audio received:**
- Ensure bot is playing audio
- Check `tcp_server.send_audio = true`

**Decode errors:**
- Install opuslib: `pip install opuslib`
- Install system Opus library
- Verify 48kHz stereo decoder settings
