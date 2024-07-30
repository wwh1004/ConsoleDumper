using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

	static void RedirectTests(Process process) {
		var inputFilter = new StringFilter(s => {
			var t = s.TrimEnd('\r', '\n');
			if (t == "test1") {
				RemoteConsole.Debug($"Input '{t}' is filtered.");
				return [];
			}
			return [s];
		});
		var inputInsertedPackets = new AsyncQueue<byte[]>();
		_ = Task.Run(async () => {
			int i = 0;
			while (true) {
				inputInsertedPackets.Enqueue(Encoding.UTF8.GetBytes($"Number {i} block was inserted.{Environment.NewLine}"));
				await Task.Delay(1000).ConfigureAwait(false);
				if (i % 10 == 5) {
					RemoteConsole.Debug("Input: ");
					Console.WriteLine("Read from remote console: ");
					Console.WriteLine(await RemoteConsole.ReadLineAsync().ConfigureAwait(false));
				}
				i++;
			}
		});
		var outputFilter = new StringFilter(s => {
			if (s.Contains("block was inserted")) {
				RemoteConsole.Debug($"Output '{s.Replace("\r", "\\r").Replace("\n", "\\n")}' is filtered.");
				return [];
			}
			return [s];
		});

		_ = RedirectMessageAsync(
			Stdio.In,
			new PassthroughReader(Console.OpenStandardInput()),
			new PassthroughWriter(process.StandardInput.BaseStream),
			new LineFeedSplitter(),
			[inputFilter],
			inputInsertedPackets);
		_ = RedirectMessageAsync(Stdio.Out,
			new PassthroughReader(process.StandardOutput.BaseStream),
			new PassthroughWriter(Console.OpenStandardOutput()),
			new LineFeedSplitter(),
			[outputFilter]);
		_ = RedirectMessageAsync(Stdio.Err,
			new PassthroughReader(process.StandardError.BaseStream),
			new PassthroughWriter(Console.OpenStandardError()),
			PassthroughSplitter.Instance,
			[]);
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
