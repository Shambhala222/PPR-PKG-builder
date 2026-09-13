namespace LibProsperoPkg.Gui.Services;

internal sealed class ExFatInspect
{
	public string AppRoot { get; init; } = "";

	public int Files { get; init; }

	public bool HasEboot { get; init; }

	public string? ParamJson { get; init; }

	public byte[]? IconPng { get; init; }
}
