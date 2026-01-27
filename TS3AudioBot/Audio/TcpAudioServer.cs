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
		private readonly ConfTcpAudioServer config;
		private TcpListener? listener;
		private CancellationTokenSource? cancellationTokenSource;
		private Task? listenerTask;
		private readonly List<TcpClient> connectedClients = new List<TcpClient>();
		private readonly object clientLock = new object();
		
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
				var buffer = new byte[4096];
				
				while (!cancellationToken.IsCancellationRequested && client.Connected)
				{
					// Read length header (4 bytes)
					int bytesRead = await stream.ReadAsync(buffer, 0, 4, cancellationToken);
					if (bytesRead != 4) break;

					int length = BitConverter.ToInt32(buffer, 0);
					if (length <= 0 || length > buffer.Length)
					{
						Log.Warn("Invalid packet length received from client: {0}", length);
						break;
					}

					// Read codec byte
					bytesRead = await stream.ReadAsync(buffer, 0, 1, cancellationToken);
					if (bytesRead != 1) break;
					
					byte codecByte = buffer[0];

					// Read audio data
					int totalRead = 0;
					while (totalRead < length)
					{
						bytesRead = await stream.ReadAsync(buffer, totalRead, length - totalRead, cancellationToken);
						if (bytesRead == 0) break;
						totalRead += bytesRead;
					}

					if (totalRead != length)
					{
						Log.Warn("Incomplete packet received from client");
						break;
					}

					// TODO: Inject received audio into the bot's audio pipeline (PassiveMergePipe)
					// This would require access to the Player/PlayManager to inject audio
					Log.Debug("Received {0} bytes of audio data from TCP client", length);
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

			// Send to all connected clients
			var clientsToRemove = new List<TcpClient>();
			foreach (var client in clientsSnapshot)
			{
				try
				{
					if (client.Connected)
					{
						var stream = client.GetStream();
						stream.Write(packet, 0, packet.Length);
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
		}
	}
}
