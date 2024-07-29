using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

enum Stdio {
	In,
	Out,
	Err,
	Dbg
}

static class Program {
	static async Task Main(string[] args) {
		try {
			if (RemoteConsole.IsServerProcess(args, out int port)) {
				Console.Title = $"RemoteConsole {port}";
				await RemoteConsole.RunAsServerAsync(port).ConfigureAwait(false);
				return;
			}

			RemoteConsole.Launch();

			var exePath = Path.Combine(Path.ChangeExtension(typeof(Program).Assembly.Location, ".x.exe"));
			using var process = Process.Start(new ProcessStartInfo(exePath, Environment.CommandLine) {
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			});

			RedirectGeneral(process);

			process.WaitForExit();
		}
		catch (Exception ex) {
			Console.WriteLine(ex);
		}
	}

	static void RedirectGeneral(Process process) {
		_ = RedirectMessageAsync(Stdio.In, new PassthroughReader(Console.OpenStandardInput()), new PassthroughWriter(process.StandardInput.BaseStream), PassthroughSplitter.Instance, []);
		_ = RedirectMessageAsync(Stdio.Out, new PassthroughReader(process.StandardOutput.BaseStream), new PassthroughWriter(Console.OpenStandardOutput()), PassthroughSplitter.Instance, []);
		_ = RedirectMessageAsync(Stdio.Err, new PassthroughReader(process.StandardError.BaseStream), new PassthroughWriter(Console.OpenStandardError()), PassthroughSplitter.Instance, []);
	}

	static async Task RedirectMessageAsync(Stdio type, IStreamReader reader, IStreamWriter writer, IStreamSplitter splitter, IEnumerable<IPacketFilter> filters, AsyncQueue<byte[]>? insertedPackets = null) {
		bool eof = false;
		var semaphore = new SemaphoreSlim(1, 1);
		if (insertedPackets is not null) {
			_ = Task.Run(async () => {
				try {
					while (true) {
						var packet = await insertedPackets.DequeueAsync().ConfigureAwait(false);
						if (Volatile.Read(ref eof))
							return;
						await WritePacketAsync(packet).ConfigureAwait(false);
					}
				}
				catch (Exception ex) {
					RemoteConsole.Debug($"Exception in RedirectMessageAsync({type}): {ex}");
				}
			});
		}
		try {
			while (true) {
				// Read a fragment
				var data = await reader.ReadFragmentAsync().ConfigureAwait(false);
				if (data.Length == 0) {
					Volatile.Write(ref eof, true);
					break;
				}
				//Console.Write($"[DBG: {type}: {Encoding.UTF8.GetString(data).Replace("\r", "\\r").Replace("\n", "\\n")}]");

				// Do split
				var packets = splitter.Split(data).ToArray();

				// Do filter
				foreach (var filter in filters)
					packets = packets.SelectMany(filter.Filter).ToArray();

				// Write the packets
				foreach (var packet in packets)
					await WritePacketAsync(packet).ConfigureAwait(false);
			}
		}
		catch (Exception ex) {
			RemoteConsole.Debug($"Exception in RedirectMessageAsync({type}): {ex}");
		}

		async Task WritePacketAsync(byte[] packet) {
			await semaphore.WaitAsync().ConfigureAwait(false);
			try {
				RemoteConsole.Write(type, packet);
				await writer.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
			}
			finally {
				semaphore.Release();
			}
		}
	}
}

static class RemoteConsole {
	static readonly AsyncQueue<string> rxQueue = new();
	static readonly AsyncQueue<string> txQueue = new();

	static Task[] StartMessageQueues(Stream stream, CancellationToken cancellationToken) {
		var rxTask = Task.Run(async () => {
			using var reader = new StreamReader(stream);
			while (true) {
				var message = await reader.ReadLineAsync(/*cancellationToken*/).ConfigureAwait(false);
				if (message is null)
					return;
				rxQueue.Enqueue(message);
			}
		});
		var txTask = Task.Run(async () => {
			using var writer = new StreamWriter(stream);
			while (true) {
				var message = await txQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);
				await writer.WriteLineAsync(message/*, cancellationToken*/).ConfigureAwait(false);
				await writer.FlushAsync(/*cancellationToken*/).ConfigureAwait(false);
			}
		});
		return [rxTask, txTask];
	}

	public static bool IsServerProcess(string[] args, out int port) {
		port = 0;
		return args.Length == 2 && args[0] == "@CONSOLE" && int.TryParse(args[1], out port);
	}

	#region Server
	public static async Task RunAsServerAsync(int port) {
		using var client = new TcpClient();
		client.Connect(new IPEndPoint(IPAddress.Loopback, port));
		using var stream = client.GetStream();
		var mqTasks = StartMessageQueues(stream, default);
		var onRxTask = Task.Run(async () => {
			while (true) {
				var message = await rxQueue.DequeueAsync().ConfigureAwait(false);
				var tokens = message.Split(',');
				switch (tokens[0]) {
				case "R":
					var line = Console.ReadLine();
					txQueue.Enqueue(Convert.ToBase64String(Encoding.UTF8.GetBytes(line)));
					break;
				case "W":
					var type = (Stdio)int.Parse(tokens[1]);
					var data = Convert.FromBase64String(tokens[2]);
					ServerWrite(type, data);
					break;
				}
			}
		});
		await Task.WhenAny([.. mqTasks, onRxTask]);
	}

	static void ServerWrite(Stdio type, byte[] data) {
		var oldColor = Console.ForegroundColor;
		Console.ForegroundColor = type switch {
			Stdio.In => ConsoleColor.Yellow,
			Stdio.Out => ConsoleColor.Green,
			Stdio.Err => ConsoleColor.Red,
			Stdio.Dbg => ConsoleColor.Gray,
			_ => throw new InvalidOperationException()
		};
		var typeString = type switch {
			Stdio.In => "IN",
			Stdio.Out => "OUT",
			Stdio.Err => "ERR",
			Stdio.Dbg => "DBG",
			_ => throw new InvalidOperationException()
		};
		Console.Write($"{typeString}: {Encoding.UTF8.GetString(data)}");
		if (data[data.Length - 1] != '\n')
			Console.WriteLine();
		Console.ForegroundColor = oldColor;
	}
	#endregion

	#region Client
	static Process? serverProcess;
	static TcpListener? clientListener;
	static Task? clientTask;
	static CancellationTokenSource? clientCts;

	public static bool Launch() {
		if (serverProcess is not null)
			return false;

		if (clientListener is null) {
			clientListener = new TcpListener(IPAddress.Loopback, 0);
			clientListener.Start();
		}

		if (clientTask is not null) {
			clientCts!.Cancel();
			clientTask.Wait();
		}

		serverProcess = Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location, $"@CONSOLE {((IPEndPoint)clientListener.LocalEndpoint).Port}") {
			UseShellExecute = true,
			CreateNoWindow = false
		});
		serverProcess.Exited += (_, _) => serverProcess = null;
		serverProcess.EnableRaisingEvents = true;

		clientCts = new CancellationTokenSource();
		clientTask = RunAsClientAsync(clientListener, clientCts.Token);
		return true;
	}

	/*public*/
	static async Task RunAsClientAsync(TcpListener listener, CancellationToken cancellationToken = default) {
		using var client = listener.AcceptTcpClient();
		using var stream = client.GetStream();
		var mqTasks = StartMessageQueues(stream, cancellationToken);
		await Task.WhenAny(mqTasks);
	}

	public static void Debug(string text, bool newLine = false) {
		if (newLine)
			text = Environment.NewLine + text + Environment.NewLine;
		Write(Stdio.Dbg, Encoding.UTF8.GetBytes(text));
	}

	public static void Write(Stdio type, byte[] data) {
		txQueue.Enqueue($"W,{(int)type},{Convert.ToBase64String(data)}");
	}

	public static async Task<string> ReadLineAsync() {
		using var _ = new LaunchConsoleHolder();
		txQueue.Enqueue("R");
		var message = await rxQueue.DequeueAsync().ConfigureAwait(false);
		return Encoding.UTF8.GetString(Convert.FromBase64String(message));
	}

	readonly struct LaunchConsoleHolder : IDisposable {
		readonly bool kill;
		public LaunchConsoleHolder() { kill = Launch(); }
		public void Dispose() { if (kill) serverProcess?.Kill(); }
	}
	#endregion
}

sealed class AsyncQueue<T> {
	readonly ConcurrentQueue<T> queue = [];
	readonly SemaphoreSlim signal = new(0);

	public void Enqueue(T item) {
		queue.Enqueue(item);
		signal.Release();
	}

	public async Task<T> DequeueAsync(CancellationToken cancellationToken = default) {
		await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
		bool b = queue.TryDequeue(out var item);
		Debug.Assert(b);
		return item;
	}
}
