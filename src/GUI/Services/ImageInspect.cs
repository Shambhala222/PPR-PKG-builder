namespace LibProsperoPkg.Gui.Services;

internal sealed class ImageInspect
{
	public string Kind { get; init; } = "";

	public string AppRoot { get; init; } = "";

	public int Files { get; init; }

	public bool HasEboot { get; init; }

	public string? ParamJson { get; init; }

	public byte[]? IconPng { get; init; }
}
