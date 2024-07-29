using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

interface IStreamReader {
	Task<byte[]> ReadFragmentAsync(CancellationToken cancellationToken = default);
}

sealed class PassthroughReader(Stream stream, int bufferSize = 4096) : IStreamReader {
	readonly byte[] buffer = new byte[bufferSize];

	public async Task<byte[]> ReadFragmentAsync(CancellationToken cancellationToken = default) {
		int count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
		if (count == 0)
			return [];

		var data = new byte[count];
		Buffer.BlockCopy(buffer, 0, data, 0, count);
		return data;
	}
}

sealed class TimeStrategyReader(Func<byte[], int, int, CancellationToken, Task<int>> readAsync, int timeout, int bufferSize = 4096) : IStreamReader {
	readonly byte[] buffer = new byte[bufferSize];
	readonly MemoryStream fragment = new();
	Task<int>? readingTask;

	public TimeStrategyReader(Stream stream, int timeout, int bufferSize = 4096)
		: this(stream.ReadAsync, timeout, bufferSize) {
	}

	public async Task<byte[]> ReadFragmentAsync(CancellationToken cancellationToken = default) {
		fragment.SetLength(0);

		int bytesRead = await readAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
		if (bytesRead == 0)
			return [];
		fragment.Write(buffer, 0, bytesRead);

		while (true) {
			readingTask ??= readAsync(buffer, 0, buffer.Length, cancellationToken);
			if (readingTask.IsCompleted) {
				bytesRead = readingTask.ConfigureAwait(false).GetAwaiter().GetResult();
				readingTask = null;
				fragment.Write(buffer, 0, bytesRead);
				if (bytesRead == 0)
					return fragment.ToArray();
				continue;
			}

			var cts = new CancellationTokenSource();
			if (await Task.WhenAny(readingTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false) != readingTask)
				return fragment.ToArray();
			cts.Cancel();
		}
	}
}
