using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

interface IStreamSplitter {
	IEnumerable<byte[]> Split(byte[] input);
}

sealed class PassthroughSplitter : IStreamSplitter {
	public static readonly PassthroughSplitter Instance = new();

	private PassthroughSplitter() { }

	public IEnumerable<byte[]> Split(byte[] input) {
		return [input];
	}
}

abstract class StateMachineStreamSplitter : IStreamSplitter {
	protected readonly BufferedStream stream = new();
	protected readonly MemoryStream buffer = new();
	Task<byte[]>? readingTask;

	public IEnumerable<byte[]> Split(byte[] input) {
		stream.SetNextBufferAndMoveNext(input);
		while ((readingTask ??= BufferResetAndReadPacketAsync()).IsCompleted) {
			var result = readingTask.ConfigureAwait(false).GetAwaiter().GetResult();
			readingTask = null;
			yield return result;
		}
	}

	Task<byte[]> BufferResetAndReadPacketAsync() {
		buffer.SetLength(0);
		return ReadPacketAsync();
	}

	protected abstract Task<byte[]> ReadPacketAsync();

	protected class BufferedStream : Stream {
		readonly byte[] readByteBuffer = new byte[1];
		TaskCompletionSource<object?> bufferTcs = new();
		byte[]? nextBuffer;
		byte[]? currentBuffer;
		int position;

		public override bool CanRead => true;

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			if (offset < 0)
				throw new ArgumentOutOfRangeException(nameof(offset));
			if (count < 0 || buffer.Length - offset < count)
				throw new ArgumentOutOfRangeException(nameof(count));

			int bytesRead = ReadBuffer(buffer, offset, count);
			if (bytesRead != 0)
				return bytesRead;

			await bufferTcs.Task.ConfigureAwait(false);
			bufferTcs = new();
			currentBuffer = nextBuffer;
			nextBuffer = null;
			position = 0;

			return ReadBuffer(buffer, offset, count);
		}

		public async Task ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) {
			while (count != 0) {
				int bytesRead = await ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
				offset += bytesRead;
				count -= bytesRead;
			}
		}

		public async Task<byte> ReadByteAsync(CancellationToken cancellationToken = default) {
			return await ReadAsync(readByteBuffer, 0, 1, cancellationToken).ConfigureAwait(false) == 1 ? readByteBuffer[0] : throw new EndOfStreamException();
		}

		int ReadBuffer(byte[] buffer, int offset, int count) {
			if (currentBuffer is null)
				return 0;
			int bytesRead = Math.Min(currentBuffer.Length - position, count);
			Buffer.BlockCopy(currentBuffer, position, buffer, offset, bytesRead);
			position += bytesRead;
			return bytesRead;
		}

		public void SetNextBufferAndMoveNext(byte[] buffer) {
			nextBuffer = buffer;
			bufferTcs.SetResult(null);
		}

		#region Not supported
		public override bool CanWrite => false;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException("Use async version instead."); }
		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
		public override void Flush() { throw new NotSupportedException(); }
		public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
		public override void SetLength(long length) { throw new NotSupportedException(); }
		protected override void Dispose(bool disposing) { }
		#endregion
	}
}

sealed class LineFeedSplitter : StateMachineStreamSplitter {
	protected override async Task<byte[]> ReadPacketAsync() {
		byte b;
		do {
			b = await stream.ReadByteAsync().ConfigureAwait(false);
			buffer.WriteByte(b);
		} while (b != '\n');
		return buffer.ToArray();
	}
}
