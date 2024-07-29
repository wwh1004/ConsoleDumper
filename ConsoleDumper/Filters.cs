using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

interface IPacketFilter {
	IEnumerable<byte[]> Filter(byte[] data);
}

sealed class RawFilter(Func<byte[], IEnumerable<byte[]>> filter) : IPacketFilter {
	public IEnumerable<byte[]> Filter(byte[] data) {
		return filter(data);
	}
}

sealed class StringFilter(Func<string, IEnumerable<string>> filter, Encoding encoding) : IPacketFilter {
	public StringFilter(Func<string, IEnumerable<string>> filter)
		: this(filter, Encoding.UTF8) {
	}

	public IEnumerable<byte[]> Filter(byte[] data) {
		var s = encoding.GetString(data);
		return filter(s).Select(encoding.GetBytes);
	}
}
