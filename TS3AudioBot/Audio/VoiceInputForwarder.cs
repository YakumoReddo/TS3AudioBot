// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using TSLib.Audio;

namespace TS3AudioBot.Audio
{
	/// <summary>
	/// Pipe that captures incoming voice from TeamSpeak users and forwards it to the TcpAudioServer.
	/// This allows external processors (like Python AI scripts) to receive and process user voice.
	/// </summary>
	public class VoiceInputForwarder : IAudioPassiveConsumer
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly TcpAudioServer tcpServer;

		/// <summary>
		/// Always active - we want to capture all incoming voice.
		/// </summary>
		public bool Active => true;

		public VoiceInputForwarder(TcpAudioServer tcpServer)
		{
			this.tcpServer = tcpServer ?? throw new ArgumentNullException(nameof(tcpServer));
		}

		/// <summary>
		/// Called when voice data is received from a TeamSpeak user.
		/// Forwards the data to the TcpAudioServer for external processing.
		/// </summary>
		/// <param name="data">The encoded audio data (typically Opus)</param>
		/// <param name="meta">Metadata including the sender's ClientId and codec info</param>
		public void Write(Span<byte> data, Meta? meta)
		{
			if (data.Length == 0)
				return;

			// Forward the voice input to TCP clients
			tcpServer.WriteVoiceInput(data, meta);
		}
	}
}
