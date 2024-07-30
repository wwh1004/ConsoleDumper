using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LitJson;

static class Program {
	const string AUTH_PROVIDER = "https://github.com";

	static async Task Main(string[] args) {
		try {
			if (RemoteConsole.IsServerProcess(args, out int port)) {
				Console.Title = $"RemoteConsole {port}";
				await RemoteConsole.RunAsServerAsync(port).ConfigureAwait(false);
				return;
			}

			var exePath = typeof(Program).Assembly.Location;
			bool showConsole = Debugger.IsAttached || File.Exists(Path.Combine(Path.GetDirectoryName(exePath), "SHOWCONSOLE"));
			if (showConsole)
				RemoteConsole.Launch();

			exePath = Path.Combine(Path.ChangeExtension(exePath, ".x.exe"));
			using var process = Process.Start(new ProcessStartInfo(exePath, Environment.CommandLine) {
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			});

			RedirectCopilotAgent(process);

			process.WaitForExit();
		}
		catch (Exception ex) {
			Console.WriteLine(ex);
		}
	}

	static void RedirectCopilotAgent(Process process) {
		var client = new InsertedJsonRpcClient();
		int? setEditorInfoReqId = null;
		var setEditorInfoRespTcs = new TaskCompletionSource<object?>();
		var inputFilter = new JsonRpcFilter(message => {
			// Parse the message
			var json = message.Content;
			var obj = JsonMapper.ToObject(json);
			var method = (obj.ContainsKey("method") && obj["method"]?.IsString == true) ? (string)obj["method"] : null;
			int? idNum = (obj.ContainsKey("id") && obj["id"]?.IsInt == true) ? (int)obj["id"] : null;

			// Do intercept
			switch (method) {
			case "initialize":
				try {
					obj["params"]["initializationOptions"]["copilotCapabilities"].Remove("token");
					RemoteConsole.Debug("initializationOptions.copilotCapabilities.token has been removed to initialize.");
				}
				catch {
				}
				return [JsonRpcMessage.Create(obj.ToJson())];
			case "setEditorInfo":
				try {
					obj["params"][0]["authProvider"] = new JsonData {
						["url"] = AUTH_PROVIDER
					};
					RemoteConsole.Debug("authProvider has been added to setEditorInfo.");
				}
				catch (Exception ex) {
					RemoteConsole.Debug($"Failed to modify setEditorInfo: {ex}");
				}
				setEditorInfoReqId = idNum;
				return [JsonRpcMessage.Create(obj.ToJson())];
			case "github/didChangeAuth":
				RemoteConsole.Debug("github/didChangeAuth has been filtered.");
				return [];
			}

			return [message];
		});
		var outputFilter = new JsonRpcFilter(message => {
			// Parse the message
			var json = message.Content;
			var obj = JsonMapper.ToObject(json);
			int? idNum = (obj.ContainsKey("id") && obj["id"]?.IsInt == true) ? (int)obj["id"] : null;
			var idStr = (obj.ContainsKey("id") && obj["id"]?.IsString == true) ? (string)obj["id"] : null;

			// Do intercept
			if (idNum.HasValue && setEditorInfoReqId.HasValue && idNum.Value == setEditorInfoReqId.Value)
				setEditorInfoRespTcs.TrySetResult(null);
			if (idStr is not null && client.IncomingResponses.TryRemove(idStr, out var responseTcs)) {
				responseTcs.SetResult(message);
				return [];
			}

			return [message];
		});
		_ = Task.Run(async () => {
			await setEditorInfoRespTcs.Task.ConfigureAwait(false);
			RemoteConsole.Debug("setEditorInfoTcs set.");

			var response = await client.InvokeAsync("checkStatus", new JsonData {
				["localChecksOnly"] = false
			});
			if ((string)response["status"] == "OK") {
				RemoteConsole.Debug("Already logged in.");
				return;
			}

			response = await client.InvokeAsync("signOut", JsonMapper.ToObject("{}"));
			if ((string)response["status"] != "NotSignedIn")
				RemoteConsole.Debug("Unexpected signOut response.");

			RemoteConsole.Debug($"Do device flow login.");
			response = await client.InvokeAsync("signInInitiate", JsonMapper.ToObject("{}"));
			if ((string)response["status"] == "PromptUserDeviceFlow") {
				Process.Start(new ProcessStartInfo((string)response["verificationUri"]) { UseShellExecute = true }).Dispose();
				RemoteConsole.Debug(
					Environment.NewLine + Environment.NewLine + Environment.NewLine +
					$"******User code: {(string)response["userCode"]}******" +
					Environment.NewLine + Environment.NewLine + Environment.NewLine);
			}
			else {
				RemoteConsole.Debug("Unexpected signInInitiate response.");
				return;
			}

			RemoteConsole.Launch();
			RemoteConsole.Debug("After entering user code, this console can be safely closed!");

			response = await client.InvokeAsync("signInConfirm", JsonMapper.ToObject("{}"));
			if ((string)response["status"] == "OK")
				RemoteConsole.Debug("Logged in successfully.");
			else
				RemoteConsole.Debug("Unexpected signInConfirm response.");
		});

		_ = RedirectMessageAsync(
			Stdio.In,
			new PassthroughReader(Console.OpenStandardInput()),
			new PassthroughWriter(process.StandardInput.BaseStream),
			new JsonRpcSplitter(),
			[inputFilter],
			client.OutgoingRequests);
		_ = RedirectMessageAsync(Stdio.Out,
			new PassthroughReader(process.StandardOutput.BaseStream),
			new PassthroughWriter(Console.OpenStandardOutput()),
			new JsonRpcSplitter(),
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

sealed class InsertedJsonRpcClient {
	public AsyncQueue<byte[]> OutgoingRequests { get; } = new();

	public ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> IncomingResponses { get; } = [];

	public async Task<JsonData> InvokeAsync(string method, JsonData @params) {
		// Send the request
		var id = Guid.NewGuid().ToString("d");
		var request = new JsonData {
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
			["params"] = @params
		};
		var responseTcs = new TaskCompletionSource<JsonRpcMessage>();
		bool b = IncomingResponses.TryAdd(id, responseTcs);
		Debug.Assert(b);
		OutgoingRequests.Enqueue(JsonRpcMessage.Create(request.ToJson()).Serialize());
		RemoteConsole.Debug($"Request {id} was injected: {request.ToJson()}");

		// Wait for the response
		var responseMessage = await responseTcs.Task.ConfigureAwait(false);
		var response = JsonMapper.ToObject(responseMessage.Content)["result"];
		RemoteConsole.Debug($"Response {id} was received: {response.ToJson()}");
		return response;
	}
}

sealed class JsonRpcMessage {
	public List<string> Headers { get; } = [];

	public string Content { get; set; } = string.Empty;

	public static JsonRpcMessage Create(string content) {
		if (string.IsNullOrEmpty(content))
			throw new ArgumentException($"'{nameof(content)}' cannot be null or empty.", nameof(content));

		var result = new JsonRpcMessage();
		result.Headers.Add($"Content-Length: {content.Length}");
		result.Content = content;
		return result;
	}

	public static JsonRpcMessage Deserialize(byte[] data) {
		if (data is null)
			throw new ArgumentNullException(nameof(data));

		var result = new JsonRpcMessage();
		var reader = new StreamReader(new MemoryStream(data));
		string header;
		while ((header = reader.ReadLine()).Length != 0)
			result.Headers.Add(header);
		result.Content = reader.ReadToEnd();
		return result;
	}

	public byte[] Serialize() {
		var buffer = new MemoryStream();
		using (var writer = new StreamWriter(buffer)) {
			foreach (var header in Headers)
				writer.WriteLine(header);
			writer.WriteLine();
			writer.Write(Content);
		}
		return buffer.ToArray();
	}
}

sealed class JsonRpcFilter(Func<JsonRpcMessage, IEnumerable<JsonRpcMessage>> filter) : IPacketFilter {
	public IEnumerable<byte[]> Filter(byte[] data) {
		var message = JsonRpcMessage.Deserialize(data);
		return filter(message).Select(t => t.Serialize());
	}
}

sealed class JsonRpcSplitter() : StateMachineStreamSplitter {
	protected override async Task<byte[]> ReadPacketAsync() {
		// Read headers
		int contentLength = -1;
		int count;
		while ((count = await ReadLineAsync().ConfigureAwait(false)) != 2) {
			var header = Encoding.UTF8.GetString(buffer.GetBuffer(), (int)buffer.Position - count, count - 2);
			if (header.StartsWith("Content-Length", StringComparison.Ordinal))
				contentLength = int.Parse(header.Split(':')[1].Trim());
		}
		if (contentLength == -1)
			throw new InvalidDataException("Content-Length not found.");

		// Read the content
		buffer.SetLength(buffer.Position + contentLength);
		await stream.ReadExactlyAsync(buffer.GetBuffer(), (int)buffer.Position, contentLength).ConfigureAwait(false);
		buffer.Position += contentLength;

		return buffer.ToArray();
	}

	async Task<int> ReadLineAsync() {
		int count = 0;
		byte b1;
		byte b2 = 0;
		do {
			b1 = b2;
			b2 = await stream.ReadByteAsync().ConfigureAwait(false);
			buffer.WriteByte(b2);
			count++;
		} while (b1 != '\r' || b2 != '\n');
		return count;
	}
}
