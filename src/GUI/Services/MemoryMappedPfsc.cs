using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using LibProsperoPkg.PFS;

namespace LibProsperoPkg.Gui.Services;

internal sealed class MemoryMappedPfsc : IDisposable
{
	private readonly MemoryMappedFile _mmf;

	private readonly MemoryMappedViewAccessor _view;

	public ProsperoPfscReader Reader { get; }

	public long DataLength => Reader.DataLength;

	private MemoryMappedPfsc(MemoryMappedFile mmf, MemoryMappedViewAccessor view, ProsperoPfscReader reader)
	{
		_mmf = mmf;
		_view = view;
		Reader = reader;
	}

	public static MemoryMappedPfsc Open(string path)
	{
		MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read);
		MemoryMappedViewAccessor memoryMappedViewAccessor = memoryMappedFile.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
		return new MemoryMappedPfsc(memoryMappedFile, memoryMappedViewAccessor, new ProsperoPfscReader(memoryMappedViewAccessor));
	}

	public void Dispose()
	{
		_view.Dispose();
		_mmf.Dispose();
	}
}
