using System;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.Gui.Services;

internal sealed class FileMemoryReader : IMemoryReader, IDisposable
{
	private readonly FileStream _stream;

	public long Length { get; }

	public FileMemoryReader(string path)
	{
		_stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		Length = _stream.Length;
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		_stream.Position = pos;
		_stream.ReadExactly(buf, offset, count);
	}

	public void Dispose()
	{
		_stream.Dispose();
	}
}
