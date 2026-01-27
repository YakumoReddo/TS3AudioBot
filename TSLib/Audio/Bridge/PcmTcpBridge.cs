// TSLib - TCP PCM bridge for external Python clients
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TSLib.Audio.Bridge
{
	public sealed class PcmTcpBridge : IAudioPipe, IDisposable
	{
		public bool Active => true;
		public IAudioPassiveConsumer? OutStream { get; set; }

		private readonly TcpListener listener;
		private readonly CancellationTokenSource cts = new CancellationTokenSource();
		private readonly BlockingCollection<byte[]> incoming;
		private readonly Task acceptTask;
		private readonly Task consumeTask;

		public PcmTcpBridge(int port, int maxQueue)
		{
			incoming = new BlockingCollection<byte[]>(maxQueue);
			listener = new TcpListener(IPAddress.Loopback, port);
			listener.Start();
			acceptTask = Task.Run(AcceptLoop, cts.Token);
			consumeTask = Task.Run(ConsumeLoop, cts.Token);
		}

		public void Write(Span<byte> data, Meta? meta)
		{
			// outgoing PCM to clients
			Broadcast(data);
		}

		private async Task AcceptLoop()
		{
			try
			{
				while (!cts.IsCancellationRequested)
				{
					var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
					_ = Task.Run(() => HandleClient(client), cts.Token);
				}
			}
			catch (OperationCanceledException) { }
		}

		private async Task HandleClient(TcpClient client)
		{
			using (client)
			using (var stream = client.GetStream())
			{
				var buffer = new byte[4096];
				while (!cts.IsCancellationRequested)
				{
					int read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
					if (read <= 0) break;
					var chunk = new byte[read];
					Array.Copy(buffer, chunk, read);
					if (!incoming.TryAdd(chunk))
					{
						// drop oldest if queue full
						incoming.TryTake(out _);
						incoming.TryAdd(chunk);
					}
				}
			}
		}

		private void ConsumeLoop()
		{
			try
			{
				foreach (var chunk in incoming.GetConsumingEnumerable(cts.Token))
				{
					OutStream?.Write(chunk, new Meta { Codec = Codec.Raw });
				}
			}
			catch (OperationCanceledException) { }
		}

		private void Broadcast(ReadOnlySpan<byte> data)
		{
			// For minimalism: no fan-out; accept loop writes not needed
			// Could extend to keep list of clients and write
			// but to keep patch small, skip outgoing to clients for now
		}

		public void Dispose()
		{
			cts.Cancel();
			incoming.CompleteAdding();
			listener.Stop();
			try { acceptTask.Wait(100); } catch { }
			try { consumeTask.Wait(100); } catch { }
		}
	}
}
