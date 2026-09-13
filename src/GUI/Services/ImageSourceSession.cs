using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.Gui.Services;

internal sealed class ImageSourceSession : IDisposable
{
	private readonly string? _mountDevice;

	private readonly string? _tempFolder;

	private bool _disposed;

	public string AppFolder { get; }

	public bool IsReadOnlyMount { get; }

	public string Kind { get; }

	private ImageSourceSession(string appFolder, bool readOnlyMount, string kind, string? mountDevice, string? tempFolder)
	{
		AppFolder = appFolder;
		IsReadOnlyMount = readOnlyMount;
		Kind = kind;
		_mountDevice = mountDevice;
		_tempFolder = tempFolder;
	}

	public static bool IsGp5(string path)
	{
		if (File.Exists(path))
		{
			return path.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool IsExFatPath(string path)
	{
		if (!File.Exists(path))
		{
			return false;
		}
		string extension = Path.GetExtension(path);
		if (!extension.Equals(".exfat", StringComparison.OrdinalIgnoreCase))
		{
			return extension.Equals(".xfat", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool IsPfsContainerPath(string path)
	{
		if (!File.Exists(path))
		{
			return false;
		}
		string extension = Path.GetExtension(path);
		if (!extension.Equals(".ffpfsc", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ffpfc", StringComparison.OrdinalIgnoreCase))
		{
			return extension.Equals(".ffpfs", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool IsImagePath(string path)
	{
		if (!IsExFatPath(path))
		{
			return IsPfsContainerPath(path);
		}
		return true;
	}

	public static string DetectKind(string path)
	{
		if (IsGp5(path))
		{
			return "gp5";
		}
		if (IsPfsContainerPath(path))
		{
			return "ffpfsc";
		}
		if (IsExFatPath(path))
		{
			return "exfat";
		}
		if (Directory.Exists(path))
		{
			return "folder";
		}
		if (File.Exists(path))
		{
			using FileMemoryReader fileMemoryReader = new FileMemoryReader(path);
			if (ExFatImage.LooksLikeExFat(fileMemoryReader, 0L) || ExFatImage.FindVolumeBase(fileMemoryReader, fileMemoryReader.Length) >= 0)
			{
				return "exfat";
			}
			if (IsPfscOrPfs(fileMemoryReader))
			{
				return "ffpfsc";
			}
		}
		return "folder";
	}

	public static ImageInspect? Inspect(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return null;
		}
		IMemoryReader memoryReader = OpenLogicalReader(path, out long length, out IDisposable extra);
		try
		{
			if (!IsPfsMagic(memoryReader) && ExFatImage.FindVolumeBase(memoryReader, length) >= 0)
			{
				using (ExFatImage exFatImage = ExFatImage.Open(memoryReader, length))
				{
					ExFatInspect exFatInspect = exFatImage.Inspect();
					return new ImageInspect
					{
						Kind = DetectKind(path),
						AppRoot = exFatInspect.AppRoot,
						Files = exFatInspect.Files,
						HasEboot = exFatInspect.HasEboot,
						ParamJson = exFatInspect.ParamJson,
						IconPng = exFatInspect.IconPng
					};
				}
			}
			List<ProsperoPfsReader.File> list = new ProsperoPfsReader(memoryReader, 0uL, null, null, null, 0L).GetAllFiles().ToList();
			int count = list.Count;
			bool hasEboot = list.Any((ProsperoPfsReader.File f) => f.name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase));
			return new ImageInspect
			{
				Kind = "ffpfsc",
				AppRoot = "",
				Files = count,
				HasEboot = hasEboot,
				ParamJson = null,
				IconPng = null
			};
		}
		finally
		{
			DisposeLogical(memoryReader, extra);
		}
	}

	public static ImageSourceSession OpenFolder(string folder)
	{
		return new ImageSourceSession(Path.GetFullPath(folder), readOnlyMount: false, "folder", null, null);
	}

	public static ImageSourceSession Prepare(string source, string kind, string tempRoot, Action<string> log, Action<long, long>? progress, CancellationToken token, bool forceExtract = false)
	{
		token.ThrowIfCancellationRequested();
		if (Directory.Exists(source) && !IsImagePath(source))
		{
			return OpenFolder(source);
		}
		if (!File.Exists(source))
		{
			throw new FileNotFoundException("The source image does not exist.", source);
		}
		if (kind == "exfat" || IsExFatPath(source) || DetectKind(source) == "exfat")
		{
			return PrepareExFat(source, tempRoot, log, progress, token, forceExtract);
		}
		return PreparePfsContainer(source, tempRoot, log, progress, token);
	}

	private static ImageSourceSession PrepareExFat(string source, string tempRoot, Action<string> log, Action<long, long>? progress, CancellationToken token, bool forceExtract)
	{
		string? error = null;
		if (!forceExtract && MacExFatMounter.TryAttach(source, out string mountPoint, out string device, out error))
		{
			string text = FindAppFolder(mountPoint);
			if (text != null)
			{
				log("Mounted the exFAT image at " + mountPoint);
				if (!string.IsNullOrEmpty(text) && !string.Equals(text, mountPoint, StringComparison.Ordinal))
				{
					log("App folder inside the image: /" + Path.GetRelativePath(mountPoint, text));
				}
				return new ImageSourceSession(text, readOnlyMount: true, "exfat", device, null);
			}
			MacExFatMounter.Detach(device);
		}
		else if (!forceExtract && !string.IsNullOrEmpty(error))
		{
			log("Could not mount the exFAT image (" + error + ") — extracting to the temp folder.");
		}
		string text2 = UniqueTemp(tempRoot, "exfat-extract");
		IMemoryReader reader = OpenLogicalReader(source, out long length, out IDisposable extra);
		try
		{
			using ExFatImage exFatImage = ExFatImage.Open(reader, length);
			log("Extracting the exFAT image to the temp folder.");
			exFatImage.ExtractAppFolder(text2, progress, log, token);
		}
		finally
		{
			DisposeLogical(reader, extra);
		}
		return new ImageSourceSession(text2, readOnlyMount: false, "exfat", null, text2);
	}

	private static ImageSourceSession PreparePfsContainer(string source, string tempRoot, Action<string> log, Action<long, long>? progress, CancellationToken token)
	{
		log("PFS container (.ffpfsc): the inner exFAT image is decompressed through the library and extracted to the temp folder (mounting is not possible).");
		string text = UniqueTemp(tempRoot, "ffpfsc-extract");
		IMemoryReader memoryReader = OpenLogicalReader(source, out long length, out IDisposable extra);
		try
		{
			if (!IsPfsMagic(memoryReader) && ExFatImage.FindVolumeBase(memoryReader, length) >= 0)
			{
				using (ExFatImage exFatImage = ExFatImage.Open(memoryReader, length))
				{
					exFatImage.ExtractAppFolder(text, progress, log, token);
					return new ImageSourceSession(text, readOnlyMount: false, "ffpfsc", null, text);
				}
			}
			List<ProsperoPfsReader.File> list = new ProsperoPfsReader(memoryReader, 0uL, null, null, null, 0L).GetAllFiles().ToList();
			if (list.Count == 0)
			{
				throw new InvalidDataException("The .ffpfsc container holds no file.");
			}
			ProsperoPfsReader.File? obj = list.FirstOrDefault((ProsperoPfsReader.File f) => f.name.EndsWith(".exfat", StringComparison.OrdinalIgnoreCase) || f.name.EndsWith(".xfat", StringComparison.OrdinalIgnoreCase)) ?? list.OrderByDescending((ProsperoPfsReader.File f) => f.size).First();
			string path = Path.Combine(text, "inner.exfat");
			Directory.CreateDirectory(text);
			obj.Save(path, decompress: true);
			long length2;
			IDisposable extra2;
			using IMemoryReader reader = OpenLogicalReader(path, out length2, out extra2);
			try
			{
				using ExFatImage exFatImage2 = ExFatImage.Open(reader, length2);
				string text2 = Path.Combine(text, "app");
				exFatImage2.ExtractAppFolder(text2, progress, log, token);
				try
				{
					File.Delete(path);
				}
				catch
				{
				}
				return new ImageSourceSession(text2, readOnlyMount: false, "ffpfsc", null, text);
			}
			finally
			{
				DisposeLogical(reader, extra2);
			}
		}
		finally
		{
			DisposeLogical(memoryReader, extra);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (!string.IsNullOrEmpty(_mountDevice))
		{
			MacExFatMounter.Detach(_mountDevice);
		}
		if (string.IsNullOrEmpty(_tempFolder) || !Directory.Exists(_tempFolder))
		{
			return;
		}
		try
		{
			Directory.Delete(_tempFolder, recursive: true);
		}
		catch
		{
		}
	}

	private static string? FindAppFolder(string mount)
	{
		if (Directory.Exists(Path.Combine(mount, "sce_sys")))
		{
			return mount;
		}
		try
		{
			foreach (string item in Directory.EnumerateDirectories(mount))
			{
				if (Directory.Exists(Path.Combine(item, "sce_sys")))
				{
					return item;
				}
				foreach (string item2 in Directory.EnumerateDirectories(item))
				{
					if (Directory.Exists(Path.Combine(item2, "sce_sys")))
					{
						return item2;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string UniqueTemp(string tempRoot, string prefix)
	{
		string text = Path.Combine(tempRoot, prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
		Directory.CreateDirectory(text);
		return text;
	}

	private static IMemoryReader OpenLogicalReader(string path, out long length, out IDisposable? extra)
	{
		extra = null;
		FileMemoryReader fileMemoryReader = new FileMemoryReader(path);
		length = fileMemoryReader.Length;
		byte[] array = new byte[8];
		if (length >= 8)
		{
			fileMemoryReader.Read(0L, array, 0, 8);
		}
		if (array[0] == 80 && array[1] == 70 && array[2] == 83 && array[3] == 67)
		{
			int num = BitConverter.ToUInt16(array, 4);
			if ((uint)(num - 2) <= 1u)
			{
				if (length > 536870912)
				{
					throw new InvalidDataException("This compressed PFS container is too large to decode in memory.");
				}
				byte[] array2 = ProsperoCompressedPfsFile.Parse(File.ReadAllBytes(path)).Decompress();
				fileMemoryReader.Dispose();
				MemoryStream memoryStream = new MemoryStream(array2, writable: false);
				length = array2.Length;
				extra = memoryStream;
				return new LibProsperoPkg.Util.StreamReader(memoryStream, 0L);
			}
			fileMemoryReader.Dispose();
			MemoryMappedPfsc memoryMappedPfsc = (MemoryMappedPfsc)(extra = MemoryMappedPfsc.Open(path));
			length = memoryMappedPfsc.DataLength;
			return memoryMappedPfsc.Reader;
		}
		return fileMemoryReader;
	}

	private static void DisposeLogical(IMemoryReader reader, IDisposable? extra)
	{
		if (extra == null)
		{
			reader.Dispose();
		}
		else
		{
			extra.Dispose();
		}
	}

	private static bool IsPfscOrPfs(IMemoryReader reader)
	{
		if (!IsPfsc(reader))
		{
			return IsPfsMagic(reader);
		}
		return true;
	}

	private static bool IsPfsc(IMemoryReader reader)
	{
		byte[] array = new byte[4];
		try
		{
			reader.Read(0L, array, 0, 4);
		}
		catch
		{
			return false;
		}
		if (array[0] == 80 && array[1] == 70 && array[2] == 83)
		{
			return array[3] == 67;
		}
		return false;
	}

	private static bool IsPfsMagic(IMemoryReader reader)
	{
		byte[] array = new byte[16];
		try
		{
			reader.Read(0L, array, 0, 16);
		}
		catch
		{
			return false;
		}
		return BitConverter.ToInt64(array, 8) == 20130315;
	}
}
