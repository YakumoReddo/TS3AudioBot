// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;

namespace TS3AudioBot.Audio
{
	/// <summary>
	/// Packet types for the TCP audio protocol.
	/// </summary>
	public enum TcpPacketType : byte
	{
		/// <summary>Audio output from the bot (what the bot is playing)</summary>
		AudioOutput = 0,
		/// <summary>Voice input from TeamSpeak users (users speaking in TS)</summary>
		VoiceInput = 1,
		/// <summary>Audio data from TCP client to be played by the bot</summary>
		AudioFromClient = 2,
		/// <summary>Command packet (for interrupt, stop, etc.)</summary>
		Command = 3
	}

	/// <summary>
	/// Command types for the TCP protocol.
	/// </summary>
	public enum TcpCommandType : byte
	{
		/// <summary>Stop current audio playback immediately</summary>
		StopPlayback = 0,
		/// <summary>Clear the audio queue</summary>
		ClearQueue = 1,
		/// <summary>Pause playback</summary>
		Pause = 2,
		/// <summary>Resume playback</summary>
		Resume = 3
	}

	/// <summary>
	/// TCP server for audio streaming. Allows external clients to receive and send audio.
	/// 
	/// Protocol format (enhanced):
	/// - Audio Output (bot playing): [Length:4][PacketType:1=0][Codec:1][Data]
	/// - Voice Input (TS users):     [Length:4][PacketType:1=1][SenderId:2][Codec:1][Data]
	/// - Audio From Client:          [Length:4][PacketType:1=2][Codec:1][Data]
	/// - Command:                    [Length:4][PacketType:1=3][CommandType:1][CommandData...]
	/// 
	/// Note: Length field is the length of all data AFTER the length field itself.
	/// </summary>
	public class TcpAudioServer : IAudioPassiveConsumer, IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxPacketSize = 1024 * 1024; // 1MB maximum packet size
		private const int PcmBufferSize = 4096 * 4; // 20ms stereo @48kHz
		private readonly ConfTcpAudioServer config;
		private TcpListener? listener;
		private CancellationTokenSource? cancellationTokenSource;
		private Task? listenerTask;
		private readonly List<TcpClient> connectedClients = new List<TcpClient>();
		private readonly object clientLock = new object();
		private readonly object inputLock = new object();
		private PassiveMergePipe? inputTarget;
		private TcpAudioProducer? inputProducer;
		private OpusDecoder? musicDecoder;
		private OpusDecoder? voiceDecoder;
		private readonly byte[] decodeBuffer = new byte[PcmBufferSize];

		/// <summary>
		/// Event triggered when a stop/interrupt command is received from a TCP client.
		/// </summary>
		public event Action? OnStopRequested;

		/// <summary>
		/// Event triggered when a clear queue command is received.
		/// </summary>
		public event Action? OnClearQueueRequested;

		/// <summary>
		/// Event triggered when a pause command is received.
		/// </summary>
		public event Action? OnPauseRequested;

		/// <summary>
		/// Event triggered when a resume command is received.
		/// </summary>
		public event Action? OnResumeRequested;

		/// <summary>
		/// Event triggered when audio data is received and enqueued from a TCP client.
		/// This should be used to unpause the audio timer if needed.
		/// </summary>
		public event Action? OnAudioReceived;

		public bool Active
		{
			get
			{
				lock (clientLock)
				{
					return config.SendAudio && connectedClients.Count > 0;
				}
			}
		}

		public TcpAudioServer(ConfTcpAudioServer config)
		{
			this.config = config;
		}

		public void SetInputTarget(PassiveMergePipe target)
		{
			lock (inputLock)
			{
				inputTarget = target;
				if (config.ReceiveAudio)
					EnsureInputAttached();
			}
		}

		public void Start()
		{
			if (!config.Enabled)
			{
				Log.Info("TCP audio server is disabled in configuration");
				return;
			}

			try
			{
				listener = new TcpListener(IPAddress.Any, config.Port);
				listener.Start();
				cancellationTokenSource = new CancellationTokenSource();
				listenerTask = Task.Run(() => AcceptClientsAsync(cancellationTokenSource.Token));
				Log.Info("TCP audio server started on port {0}", config.Port);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to start TCP audio server on port {0}", config.Port);
				throw;
			}
		}

		private async Task AcceptClientsAsync(CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested && listener != null)
			{
				try
				{
					var client = await listener.AcceptTcpClientAsync();
					Log.Info("TCP audio client connected: {0}", client.Client.RemoteEndPoint);

					lock (clientLock)
					{
						connectedClients.Add(client);
					}

					// Start receiving task for this client if enabled
					if (config.ReceiveAudio)
					{
						_ = Task.Run(() => HandleClientReceiveAsync(client, cancellationToken));
					}
				}
				catch (ObjectDisposedException)
				{
					// Listener was stopped
					break;
				}
				catch (Exception ex)
				{
					if (!cancellationToken.IsCancellationRequested)
					{
						Log.Warn(ex, "Error accepting TCP client");
					}
				}
			}
		}

		private async Task HandleClientReceiveAsync(TcpClient client, CancellationToken cancellationToken)
		{
			try
			{
				using var stream = client.GetStream();
				var headerBuffer = new byte[8]; // Maximum header size for any packet type

				while (!cancellationToken.IsCancellationRequested && client.Connected)
				{
					// Read length header (4 bytes)
					int bytesRead = await ReadExactAsync(stream, headerBuffer, 0, 4, cancellationToken);
					if (bytesRead != 4) break;

					int length = BitConverter.ToInt32(headerBuffer, 0);
					if (length <= 0 || length > MaxPacketSize)
					{
						Log.Warn("Invalid packet length received from client: {0}", length);
						break;
					}

					// Read packet type (1 byte)
					bytesRead = await ReadExactAsync(stream, headerBuffer, 0, 1, cancellationToken);
					if (bytesRead != 1) break;

					var packetType = (TcpPacketType)headerBuffer[0];
					int remainingLength = length - 1; // Subtract packet type byte

					switch (packetType)
					{
						case TcpPacketType.AudioFromClient:
							await HandleAudioFromClientAsync(stream, headerBuffer, remainingLength, cancellationToken);
							break;
						case TcpPacketType.Command:
							await HandleCommandAsync(stream, headerBuffer, remainingLength, cancellationToken);
							break;
						default:
							// Skip unknown packet types
							var skipBuffer = new byte[remainingLength];
							await ReadExactAsync(stream, skipBuffer, 0, remainingLength, cancellationToken);
							Log.Warn("Received unknown packet type: {0}", packetType);
							break;
					}
				}
			}
			catch (Exception ex)
			{
				if (!cancellationToken.IsCancellationRequested)
				{
					Log.Debug(ex, "Error receiving from TCP client");
				}
			}
			finally
			{
				RemoveClient(client);
			}
		}

		private async Task HandleAudioFromClientAsync(NetworkStream stream, byte[] headerBuffer, int remainingLength, CancellationToken cancellationToken)
		{
			if (remainingLength < 1)
			{
				Log.Warn("Audio packet too short");
				return;
			}

			// Read codec byte
			int bytesRead = await ReadExactAsync(stream, headerBuffer, 0, 1, cancellationToken);
			if (bytesRead != 1) return;

			byte codecByte = headerBuffer[0];
			int audioLength = remainingLength - 1;

			if (audioLength <= 0)
			{
				Log.Warn("Audio packet has no audio data");
				return;
			}

			// Read audio data
			byte[] audioBuffer = new byte[audioLength];
			bytesRead = await ReadExactAsync(stream, audioBuffer, 0, audioLength, cancellationToken);
			if (bytesRead != audioLength)
			{
				Log.Warn("Incomplete audio packet received from client");
				return;
			}

			if (!TryDecode(codecByte, audioBuffer, audioLength, out var decoded))
				return;

			EnqueueDecoded(decoded);
		}

		private async Task HandleCommandAsync(NetworkStream stream, byte[] headerBuffer, int remainingLength, CancellationToken cancellationToken)
		{
			if (remainingLength < 1)
			{
				Log.Warn("Command packet too short");
				return;
			}

			// Read command type
			int bytesRead = await ReadExactAsync(stream, headerBuffer, 0, 1, cancellationToken);
			if (bytesRead != 1) return;

			var commandType = (TcpCommandType)headerBuffer[0];

			// Read any additional command data (if present)
			int commandDataLength = remainingLength - 1;
			byte[]? commandData = null;
			if (commandDataLength > 0)
			{
				commandData = new byte[commandDataLength];
				bytesRead = await ReadExactAsync(stream, commandData, 0, commandDataLength, cancellationToken);
				if (bytesRead != commandDataLength)
				{
					Log.Warn("Incomplete command data received");
					return;
				}
			}

			Log.Info("Received command from TCP client: {0}", commandType);

			switch (commandType)
			{
				case TcpCommandType.StopPlayback:
					OnStopRequested?.Invoke();
					break;
				case TcpCommandType.ClearQueue:
					OnClearQueueRequested?.Invoke();
					ClearInputQueue();
					break;
				case TcpCommandType.Pause:
					OnPauseRequested?.Invoke();
					break;
				case TcpCommandType.Resume:
					OnResumeRequested?.Invoke();
					break;
				default:
					Log.Warn("Unknown command type: {0}", commandType);
					break;
			}
		}

		private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			int totalRead = 0;
			while (totalRead < count)
			{
				int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
				if (bytesRead == 0) return totalRead;
				totalRead += bytesRead;
			}
			return totalRead;
		}

		/// <summary>
		/// Writes bot audio output (what the bot is playing) to TCP clients.
		/// This is called from the audio pipeline when the bot plays audio.
		/// Protocol: [Length:4][PacketType:1=0][Codec:1][Data]
		/// </summary>
		public void Write(Span<byte> data, Meta? meta)
		{
			if (!config.SendAudio || data.Length == 0)
				return;

			List<TcpClient> clientsSnapshot;
			lock (clientLock)
			{
				if (connectedClients.Count == 0)
					return;
				clientsSnapshot = new List<TcpClient>(connectedClients);
			}

			var codec = meta?.Codec ?? Codec.OpusMusic;
			byte codecByte = (byte)codec;

			// Build packet: [Length:4][PacketType:1=0][Codec:1][Data]
			int packetLength = 1 + 1 + data.Length; // PacketType + Codec + Data
			byte[] packet = new byte[4 + packetLength];
			BitConverter.GetBytes(packetLength).CopyTo(packet, 0);
			packet[4] = (byte)TcpPacketType.AudioOutput;
			packet[5] = codecByte;
			data.CopyTo(new Span<byte>(packet, 6, data.Length));

			SendToAllClients(packet, clientsSnapshot);
		}

		/// <summary>
		/// Writes incoming voice from TeamSpeak users to TCP clients.
		/// This allows external processors (like Python AI) to receive user voice.
		/// Protocol: [Length:4][PacketType:1=1][SenderId:2][Codec:1][Data]
		/// </summary>
		/// <param name="data">The encoded audio data</param>
		/// <param name="meta">Audio metadata including sender information</param>
		public void WriteVoiceInput(Span<byte> data, Meta? meta)
		{
			if (!config.SendAudio || data.Length == 0)
				return;

			List<TcpClient> clientsSnapshot;
			lock (clientLock)
			{
				if (connectedClients.Count == 0)
					return;
				clientsSnapshot = new List<TcpClient>(connectedClients);
			}

			var codec = meta?.Codec ?? Codec.OpusVoice;
			byte codecByte = (byte)codec;
			ushort senderId = meta?.In.Sender.Value ?? 0;

			// Build packet: [Length:4][PacketType:1=1][SenderId:2][Codec:1][Data]
			int packetLength = 1 + 2 + 1 + data.Length; // PacketType + SenderId + Codec + Data
			byte[] packet = new byte[4 + packetLength];
			BitConverter.GetBytes(packetLength).CopyTo(packet, 0);
			packet[4] = (byte)TcpPacketType.VoiceInput;
			BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), senderId);
			packet[7] = codecByte;
			data.CopyTo(new Span<byte>(packet, 8, data.Length));

			SendToAllClients(packet, clientsSnapshot);
		}

		private void SendToAllClients(byte[] packet, List<TcpClient> clients)
		{
			var clientsToRemove = new List<TcpClient>();
			foreach (var client in clients)
			{
				try
				{
					if (client.Connected)
					{
						var stream = client.GetStream();
						// Fire and forget - avoid blocking audio thread
						_ = stream.WriteAsync(packet, 0, packet.Length);
					}
					else
					{
						clientsToRemove.Add(client);
					}
				}
				catch (Exception ex)
				{
					Log.Debug(ex, "Error sending audio to TCP client");
					clientsToRemove.Add(client);
				}
			}

			// Remove disconnected clients
			foreach (var client in clientsToRemove)
			{
				RemoveClient(client);
			}
		}

		private void RemoveClient(TcpClient client)
		{
			lock (clientLock)
			{
				if (connectedClients.Remove(client))
				{
					try
					{
						Log.Info("TCP audio client disconnected: {0}", client.Client.RemoteEndPoint);
						client.Close();
					}
					catch { }
				}
			}
		}

		public void Stop()
		{
			Log.Info("Stopping TCP audio server");

			cancellationTokenSource?.Cancel();

			DetachInput();

			lock (clientLock)
			{
				foreach (var client in connectedClients)
				{
					try { client.Close(); } catch { }
				}
				connectedClients.Clear();
			}

			listener?.Stop();
			listener = null;

			try
			{
				listenerTask?.Wait(TimeSpan.FromSeconds(5));
			}
			catch { }
		}

		public void Dispose()
		{
			Stop();
			cancellationTokenSource?.Dispose();
			inputProducer?.Dispose();
			musicDecoder?.Dispose();
			voiceDecoder?.Dispose();
		}

		private void EnsureInputAttached()
		{
			if (!config.ReceiveAudio)
				return;
			if (inputTarget is null)
				return;
			if (inputProducer is null)
				inputProducer = new TcpAudioProducer();
			inputTarget.Add(inputProducer);
		}

		private void DetachInput()
		{
			lock (inputLock)
			{
				if (inputTarget != null && inputProducer != null)
					inputTarget.Remove(inputProducer);
			}
		}

		/// <summary>
		/// Clears the input audio queue (for interrupt functionality).
		/// </summary>
		public void ClearInputQueue()
		{
			inputProducer?.Clear();
		}

		private bool TryDecode(byte codecByte, byte[] data, int length, out byte[]? decoded)
		{
			Log.Debug("Decoding audio packet: {0} bytes, codec: {1}", length, codecByte);
			decoded = null;
			try
			{
				if (!Enum.IsDefined(typeof(Codec), codecByte))
				{
					Log.Warn("Unsupported codec byte {0} from TCP client", codecByte);
					return false;
				}
			}
			catch (Exception ex)
			{
				Log.Error("Error while validating codec: {0}", ex.ToString());
				return false;
			}

			var codec = (Codec)codecByte;
			try
			{
				switch (codec)
				{
					case Codec.OpusMusic:
						musicDecoder ??= OpusDecoder.Create(48_000, 2);
						var musicSpan = musicDecoder.Decode(new Span<byte>(data, 0, length), decodeBuffer);
						if (musicSpan.Length == 0)
							return false;
						decoded = musicSpan.ToArray();
						return true;
					case Codec.OpusVoice:
						voiceDecoder ??= OpusDecoder.Create(48_000, 1);
						var mono = voiceDecoder.Decode(new Span<byte>(data, 0, length), decodeBuffer.AsSpan(0, decodeBuffer.Length / 2));
						if (mono.Length == 0)
							return false;
						var monoLength = mono.Length;
						if (!AudioTools.TryMonoToStereo(decodeBuffer, ref monoLength))
							return false;
						decoded = new byte[monoLength];
						Array.Copy(decodeBuffer, 0, decoded, 0, monoLength);
						return true;
					default:
						Log.Warn("Received unsupported codec {0}", codec);
						return false;
				}
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "Failed to decode TCP audio packet");
				return false;
			}
		}

		private void EnqueueDecoded(byte[]? decodedBuffer)
		{
			if (decodedBuffer is null)
				return;

			var producer = inputProducer;
			if (producer is null)
			{
				lock (inputLock)
				{
					EnsureInputAttached();
					producer = inputProducer;
				}
			}

			if (producer is null)
				return;

			producer.Enqueue(decodedBuffer);
			
			// Notify that audio has been received - this allows the player to unpause
			OnAudioReceived?.Invoke();
		}

		private class TcpAudioProducer : IAudioPassiveProducer
		{
			private readonly Queue<byte[]> buffers = new Queue<byte[]>();
			private byte[]? current;
			private int currentOffset;
			private readonly object queueLock = new object();

			public void Enqueue(byte[] data)
			{
				lock (queueLock)
				{
					buffers.Enqueue(data);
				}
			}

			public void Clear()
			{
				lock (queueLock)
				{
					buffers.Clear();
					current = null;
					currentOffset = 0;
				}
			}

			public int Read(byte[] buffer, int offset, int length, out Meta? meta)
			{
				meta = null;
				int written = 0;

				lock (queueLock)
				{
					while (written < length)
					{
						if (current is null || currentOffset >= current.Length)
						{
							if (buffers.Count == 0)
								break;
							current = buffers.Dequeue();
							currentOffset = 0;
						}

						int toCopy = Math.Min(length - written, current.Length - currentOffset);
						Array.Copy(current, currentOffset, buffer, offset + written, toCopy);
						currentOffset += toCopy;
						written += toCopy;
					}
				}

				return written;
			}

			public void Dispose()
			{
				Clear();
			}
		}
	}
}
