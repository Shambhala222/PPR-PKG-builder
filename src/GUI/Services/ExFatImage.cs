using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.Gui.Services;

internal sealed class ExFatImage : IDisposable
{
	private const int EntrySize = 32;

	private const byte TypeEnd = 0;

	private const byte TypeFile = 133;

	private const byte TypeStream = 192;

	private const byte TypeName = 193;

	private const uint FatEnd = 4294967288u;

	private const uint FatBad = 4294967287u;

	private const uint NoFatChainFlag = 2u;

	private readonly IMemoryReader _reader;

	private readonly bool _ownsReader;

	private readonly long _length;

	private readonly long _volumeBase;

	private readonly int _sectorSize;

	private readonly int _clusterSize;

	private readonly long _fatOffset;

	private readonly long _heapOffset;

	private readonly uint _rootCluster;

	private readonly uint _clusterCount;

	public long VolumeLength => _length - _volumeBase;

	public string AppRoot { get; private set; } = "";

	private ExFatImage(IMemoryReader reader, bool ownsReader, long length, long volumeBase, int sectorSize, int clusterSize, long fatOffset, long heapOffset, uint rootCluster, uint clusterCount)
	{
		_reader = reader;
		_ownsReader = ownsReader;
		_length = length;
		_volumeBase = volumeBase;
		_sectorSize = sectorSize;
		_clusterSize = clusterSize;
		_fatOffset = fatOffset;
		_heapOffset = heapOffset;
		_rootCluster = rootCluster;
		_clusterCount = clusterCount;
	}

	public static bool LooksLikeExFat(IMemoryReader reader, long offset)
	{
		byte[] array = new byte[8];
		try
		{
			reader.Read(offset + 3, array, 0, 8);
		}
		catch
		{
			return false;
		}
		if (array[0] == 69 && array[1] == 88 && array[2] == 70 && array[3] == 65 && array[4] == 84 && array[5] == 32 && array[6] == 32)
		{
			return array[7] == 32;
		}
		return false;
	}

	public static long FindVolumeBase(IMemoryReader reader, long length)
	{
		if (LooksLikeExFat(reader, 0L))
		{
			return 0L;
		}
		long num = Math.Min(length, 4194304L);
		for (long num2 = 512L; num2 + 11 <= num; num2 += 512)
		{
			if (LooksLikeExFat(reader, num2))
			{
				return num2;
			}
		}
		return -1L;
	}

	public static ExFatImage Open(IMemoryReader reader, long length, bool ownsReader = false)
	{
		long num = FindVolumeBase(reader, length);
		if (num < 0)
		{
			throw new InvalidDataException("The file is not a valid exFAT image (no EXFAT signature found).");
		}
		byte[] array = new byte[512];
		reader.Read(num, array, 0, 512);
		int num2 = array[108];
		int num3 = array[109];
		if ((num2 < 9 || num2 > 12) ? true : false)
		{
			throw new InvalidDataException("Unsupported exFAT sector size.");
		}
		int num4 = 1 << num2;
		int num5 = num4 << num3;
		uint num6 = BitConverter.ToUInt32(array, 80);
		uint num7 = BitConverter.ToUInt32(array, 88);
		uint clusterCount = BitConverter.ToUInt32(array, 92);
		uint num8 = BitConverter.ToUInt32(array, 96);
		if (num8 < 2 || num5 <= 0)
		{
			throw new InvalidDataException("The exFAT boot sector is incomplete.");
		}
		ExFatImage exFatImage = new ExFatImage(reader, ownsReader, length, num, num4, num5, num + num6 * num4, num + num7 * num4, num8, clusterCount);
		exFatImage.AppRoot = exFatImage.FindAppRoot();
		return exFatImage;
	}

	public void Dispose()
	{
		if (_ownsReader)
		{
			_reader.Dispose();
		}
	}

	public long SumAppPayloadBytes()
	{
		long bytes = 0;
		foreach (ExFatEntry item in EnumerateFiles(AppRoot))
		{
			if (!IsJunk(item.Name) && !IsJunkDirectory(item.RelativePath))
				bytes += item.Size;
		}
		return bytes;
	}

	public ExFatInspect Inspect()
	{
		string appRoot = AppRoot;
		int num = 0;
		bool hasEboot = false;
		foreach (ExFatEntry item in EnumerateFiles(appRoot))
		{
			if (!IsJunk(item.Name))
			{
				num++;
				if (item.Name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
				{
					hasEboot = true;
				}
			}
		}
		return new ExFatInspect
		{
			AppRoot = appRoot,
			Files = num,
			HasEboot = hasEboot,
			ParamJson = TryReadText(Join(appRoot, "sce_sys/param.json")),
			IconPng = TryReadBytes(Join(appRoot, "sce_sys/icon0.png"), 4194304)
		};
	}

	public void ExtractAppFolder(string destination, Action<long, long>? progress, Action<string>? log, CancellationToken token)
	{
		Directory.CreateDirectory(destination);
		List<ExFatEntry> list = new List<ExFatEntry>();
		long num = 0L;
		foreach (ExFatEntry item in EnumerateFiles(AppRoot))
		{
			if (!IsJunk(item.Name) && !IsJunkDirectory(item.RelativePath))
			{
				list.Add(item);
				num += item.Size;
			}
		}
		long num2 = 0L;
		foreach (ExFatEntry item2 in list)
		{
			token.ThrowIfCancellationRequested();
			string text = item2.RelativePath;
			if (!string.IsNullOrEmpty(AppRoot))
			{
				string text2 = AppRoot.TrimEnd('/') + "/";
				if (text.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
				{
					text = text.Substring(text2.Length);
				}
			}
			string fullPath = Path.GetFullPath(Path.Combine(destination, text.Replace('/', Path.DirectorySeparatorChar)));
			string fullPath2 = Path.GetFullPath(destination);
			if (!fullPath.StartsWith(fullPath2 + Path.DirectorySeparatorChar, StringComparison.Ordinal) && fullPath != fullPath2)
			{
				throw new InvalidDataException("Unsafe path inside the exFAT image: " + item2.RelativePath);
			}
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
			CopyFile(item2, fullPath, token);
			num2 += item2.Size;
			progress?.Invoke(num2, num);
		}
		log?.Invoke("Extracted " + list.Count.ToString("N0") + " files from the exFAT image.");
	}

	private string FindAppRoot()
	{
		if (HasSceSys(""))
		{
			return "";
		}
		foreach (ExFatEntry item in EnumerateDirectories("", 1))
		{
			if (HasSceSys(item.RelativePath))
			{
				return item.RelativePath;
			}
		}
		foreach (ExFatEntry item2 in EnumerateDirectories("", 2))
		{
			if (HasSceSys(item2.RelativePath))
			{
				return item2.RelativePath;
			}
		}
		throw new InvalidDataException("No app folder (containing sce_sys) was found inside the exFAT image.");
	}

	private bool HasSceSys(string dir)
	{
		foreach (ExFatEntry item in ReadDirectory(ClusterOfDirectory(dir), dir))
		{
			if (item.IsDirectory && item.Name.Equals("sce_sys", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private IEnumerable<ExFatEntry> EnumerateFiles(string directory)
	{
		foreach (ExFatEntry item in ReadDirectory(ClusterOfDirectory(directory), directory))
		{
			if (IsJunk(item.Name) || IsJunkDirectory(item.Name))
			{
				continue;
			}
			if (item.IsDirectory)
			{
				foreach (ExFatEntry item2 in EnumerateFiles(item.RelativePath))
				{
					yield return item2;
				}
			}
			else
			{
				yield return item;
			}
		}
	}

	private IEnumerable<ExFatEntry> EnumerateDirectories(string directory, int depth)
	{
		if (depth < 1)
		{
			yield break;
		}
		foreach (ExFatEntry entry in ReadDirectory(ClusterOfDirectory(directory), directory))
		{
			if (!entry.IsDirectory || IsJunkDirectory(entry.Name))
			{
				continue;
			}
			yield return entry;
			if (depth <= 1)
			{
				continue;
			}
			foreach (ExFatEntry item in EnumerateDirectories(entry.RelativePath, depth - 1))
			{
				yield return item;
			}
		}
	}

	private uint ClusterOfDirectory(string directory)
	{
		if (string.IsNullOrEmpty(directory))
		{
			return _rootCluster;
		}
		uint num = _rootCluster;
		string text = "";
		string[] array = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array)
		{
			ExFatEntry exFatEntry = null;
			foreach (ExFatEntry item in ReadDirectory(num, text))
			{
				if (item.IsDirectory && item.Name.Equals(text2, StringComparison.OrdinalIgnoreCase))
				{
					exFatEntry = item;
					break;
				}
			}
			if (exFatEntry == null)
			{
				throw new DirectoryNotFoundException(directory);
			}
			num = exFatEntry.FirstCluster;
			text = (string.IsNullOrEmpty(text) ? text2 : (text + "/" + text2));
		}
		return num;
	}

	private IEnumerable<ExFatEntry> ReadDirectory(uint firstCluster, string parent)
	{
		byte[] data = ReadClusterChain(firstCluster, long.MaxValue, contiguous: false);
		int i = 0;
		while (i + 32 <= data.Length)
		{
			byte b = data[i];
			if (b == 0)
			{
				break;
			}
			if ((b & 0x80) == 0)
			{
				i += 32;
				continue;
			}
			if (b != 133)
			{
				i += 32;
				continue;
			}
			int num = data[i + 1];
			int num2 = i + 32 * (1 + num);
			if (num2 > data.Length)
			{
				break;
			}
			bool isDirectory = (BitConverter.ToUInt16(data, i + 4) & 0x10) != 0;
			uint num3 = 0u;
			uint firstCluster2 = 0u;
			long size = 0L;
			int num4 = 0;
			StringBuilder stringBuilder = new StringBuilder();
			for (int j = 1; j <= num; j++)
			{
				int num5 = i + j * 32;
				switch (data[num5])
				{
				case 192:
					num3 = data[num5 + 1];
					num4 = data[num5 + 3];
					firstCluster2 = BitConverter.ToUInt32(data, num5 + 20);
					size = BitConverter.ToInt64(data, num5 + 24);
					break;
				case 193:
					stringBuilder.Append(Encoding.Unicode.GetString(data, num5 + 2, 30));
					break;
				}
			}
			string text = stringBuilder.ToString().TrimEnd('\0');
			if (num4 > 0 && text.Length > num4)
			{
				text = text.Substring(0, num4);
			}
			i = num2;
			if (!string.IsNullOrEmpty(text))
			{
				yield return new ExFatEntry
				{
					Name = text,
					RelativePath = (string.IsNullOrEmpty(parent) ? text : (parent + "/" + text)),
					IsDirectory = isDirectory,
					FirstCluster = firstCluster2,
					Size = size,
					Contiguous = ((num3 & 2) != 0)
				};
			}
		}
	}

	private string? TryReadText(string relative)
	{
		byte[] array = TryReadBytes(relative, 2097152);
		if (array == null)
		{
			return null;
		}
		return Encoding.UTF8.GetString(array);
	}

	private byte[]? TryReadBytes(string relative, int max)
	{
		try
		{
			ExFatEntry exFatEntry = FindFile(relative);
			if (exFatEntry == null || exFatEntry.Size <= 0 || exFatEntry.Size > max)
			{
				return null;
			}
			return ReadClusterChain(exFatEntry.FirstCluster, exFatEntry.Size, exFatEntry.Contiguous);
		}
		catch
		{
			return null;
		}
	}

	private ExFatEntry? FindFile(string relative)
	{
		string text = relative.Replace('\\', '/').Trim('/');
		string text2 = "";
		string value = text;
		int num = text.LastIndexOf('/');
		if (num >= 0)
		{
			text2 = text.Substring(0, num);
			value = text.Substring(num + 1);
		}
		foreach (ExFatEntry item in ReadDirectory(ClusterOfDirectory(text2), text2))
		{
			if (!item.IsDirectory && item.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}
		return null;
	}

	private void CopyFile(ExFatEntry entry, string dest, CancellationToken token)
	{
		using FileStream fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
		if (entry.Size <= 0 || entry.FirstCluster < 2)
		{
			return;
		}
		long num = entry.Size;
		foreach (uint item in ClusterSequence(entry.FirstCluster, entry.Contiguous, entry.Size))
		{
			token.ThrowIfCancellationRequested();
			int num2 = (int)Math.Min(_clusterSize, num);
			byte[] array = new byte[num2];
			_reader.Read(ClusterOffset(item), array, 0, num2);
			fileStream.Write(array, 0, num2);
			num -= num2;
			if (num <= 0)
			{
				break;
			}
		}
	}

	private byte[] ReadClusterChain(uint firstCluster, long size, bool contiguous)
	{
		if (firstCluster < 2 || size <= 0)
		{
			return Array.Empty<byte>();
		}
		long num = ((size == long.MaxValue) ? EstimateDirectoryBytes(firstCluster, contiguous) : size);
		MemoryStream memoryStream = new MemoryStream((int)((num <= int.MaxValue) ? Math.Min(num, 8388608L) : 0));
		long num2 = num;
		foreach (uint item in ClusterSequence(firstCluster, contiguous, num))
		{
			int num3 = (int)Math.Min(_clusterSize, num2);
			byte[] array = new byte[num3];
			_reader.Read(ClusterOffset(item), array, 0, num3);
			memoryStream.Write(array, 0, num3);
			num2 -= num3;
			if (num2 <= 0)
			{
				break;
			}
		}
		return memoryStream.ToArray();
	}

	private long EstimateDirectoryBytes(uint firstCluster, bool contiguous)
	{
		int num = 0;
		foreach (uint item in ClusterSequence(firstCluster, contiguous, 64L * (long)_clusterSize))
		{
			_ = item;
			num++;
			if (num >= 64)
			{
				break;
			}
		}
		return (long)num * (long)_clusterSize;
	}

	private IEnumerable<uint> ClusterSequence(uint firstCluster, bool contiguous, long size)
	{
		if (contiguous)
		{
			long need = (size + _clusterSize - 1) / _clusterSize;
			for (uint i = 0u; i < need; i++)
			{
				yield return firstCluster + i;
			}
			yield break;
		}
		HashSet<uint> seen = new HashSet<uint>();
		uint cluster = firstCluster;
		while (cluster >= 2 && cluster < 4294967288u && cluster != 4294967287u && seen.Add(cluster))
		{
			yield return cluster;
			cluster = ReadFat(cluster);
		}
	}

	private uint ReadFat(uint cluster)
	{
		if (cluster < 2 || cluster > _clusterCount + 1)
		{
			return 4294967288u;
		}
		byte[] array = new byte[4];
		_reader.Read(_fatOffset + (long)cluster * 4L, array, 0, 4);
		return BitConverter.ToUInt32(array, 0);
	}

	private long ClusterOffset(uint cluster)
	{
		return _heapOffset + (cluster - 2) * _clusterSize;
	}

	private static string Join(string root, string rel)
	{
		if (!string.IsNullOrEmpty(root))
		{
			return root.TrimEnd('/') + "/" + rel.TrimStart('/');
		}
		return rel;
	}

	private static bool IsJunk(string name)
	{
		if (!name.StartsWith("._", StringComparison.Ordinal) && !name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) && !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
		{
			return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsJunkDirectory(string path)
	{
		if (!path.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) && !path.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
		{
			return path.Contains("/System Volume Information", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
