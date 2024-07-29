using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

interface IStreamWriter {
	Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
}

sealed class PassthroughWriter(Stream stream) : IStreamWriter {
	public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) {
		await stream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync().ConfigureAwait(false);
	}
}

sealed class TimeStrategyWriter(Func<byte[], CancellationToken, Task> writeFragmentAsync, int timeout) : IStreamWriter {
	readonly MemoryStream fragment = new();
	Task? writingTask;
	CancellationTokenSource writingTaskCts = null!;

	public TimeStrategyWriter(Stream stream, int timeout)
		: this(async (buffer, cancellationToken) => {
			await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
			await stream.FlushAsync().ConfigureAwait(false);
		}, timeout) {
	}

	public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) {
		if (writingTask is not null) {
			writingTaskCts.Cancel();
			await writingTask.ConfigureAwait(false);
		}

		fragment.Write(buffer, offset, count);
		writingTaskCts = new CancellationTokenSource();
		writingTask = WriteFragmentAsync(cancellationToken);
	}

	async Task WriteFragmentAsync(CancellationToken cancellationToken) {
		try {
			await Task.Delay(timeout, writingTaskCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) {
			return;
		}

		var buffer = fragment.ToArray();
		fragment.SetLength(0);
		await writeFragmentAsync(buffer, cancellationToken).ConfigureAwait(false);
	}
}
