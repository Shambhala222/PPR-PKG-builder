namespace LibProsperoPkg.Gui.Services;

internal sealed class ExFatEntry
{
	public required string Name { get; init; }

	public required string RelativePath { get; init; }

	public bool IsDirectory { get; init; }

	public uint FirstCluster { get; init; }

	public long Size { get; init; }

	public bool Contiguous { get; init; }
}
