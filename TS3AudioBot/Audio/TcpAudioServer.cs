// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;
	using System.Threading.Tasks;
	using TS3AudioBot.Config;
	using TSLib;
	using TSLib.Audio;
	using TSLib.Audio.Opus;
	using TSLib.Helper;

namespace TS3AudioBot.Audio
{
	/// <summary>
	/// TCP server for audio streaming. Allows external clients to receive and send audio.
	/// Protocol format: [Length:4 bytes][Codec:1 byte][Data]
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
				var headerBuffer = new byte[5]; // 4 bytes length + 1 byte codec
				
				while (!cancellationToken.IsCancellationRequested && client.Connected)
				{
					// Read length header (4 bytes)
					int bytesRead = await stream.ReadAsync(headerBuffer, 0, 4, cancellationToken);
					if (bytesRead != 4) break;

					int length = BitConverter.ToInt32(headerBuffer, 0);
					if (length <= 0 || length > MaxPacketSize)
					{
						Log.Warn("Invalid packet length received from client: {0}", length);
						break;
					}

					// Read codec byte
					bytesRead = await stream.ReadAsync(headerBuffer, 0, 1, cancellationToken);
					if (bytesRead != 1) break;
					
					byte codecByte = headerBuffer[0];

					// Allocate buffer for audio data
					byte[] audioBuffer = new byte[length];
					int totalRead = 0;
					while (totalRead < length)
					{
						bytesRead = await stream.ReadAsync(audioBuffer, totalRead, length - totalRead, cancellationToken);
						if (bytesRead == 0) break;
						totalRead += bytesRead;
					}

					if (totalRead != length)
					{
						Log.Warn("Incomplete packet received from client");
						break;
					}

					if (!TryDecode(codecByte, audioBuffer, totalRead, out var decodedSpan))
						continue;

					EnqueueDecoded(decodedSpan);
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
			
			// Build packet: [Length:4][Codec:1][Data]
			byte[] packet = new byte[5 + data.Length];
			BitConverter.GetBytes(data.Length).CopyTo(packet, 0);
			packet[4] = codecByte;
			data.CopyTo(new Span<byte>(packet, 5, data.Length));

			// Send to all connected clients asynchronously
			var clientsToRemove = new List<TcpClient>();
			foreach (var client in clientsSnapshot)
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

		private bool TryDecode(byte codecByte, byte[] data, int length, out byte[]? decoded)
		{
			decoded = null;

			if (!Enum.IsDefined(typeof(Codec), (int)codecByte))
			{
				Log.Warn("Unsupported codec byte {0} from TCP client", codecByte);
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

		private void EnqueueDecoded(byte[] decodedBuffer)
		{
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
				lock (queueLock)
				{
					buffers.Clear();
					current = null;
					currentOffset = 0;
				}
			}
		}
	}
}
