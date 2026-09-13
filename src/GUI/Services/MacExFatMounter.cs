using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LibProsperoPkg.Gui.Services;

internal static class MacExFatMounter
{
	public static bool TryAttach(string imagePath, out string? mountPoint, out string? device, out string? error)
	{
		mountPoint = null;
		device = null;
		error = null;
		if (!OperatingSystem.IsMacOS())
		{
			error = "hdiutil is only available on macOS";
			return false;
		}
		string[] array = new string[2]
		{
			"attach -readonly -nobrowse -noverify -plist \"" + Escape(imagePath) + "\"",
			"attach -readonly -nobrowse -noverify -imagekey diskimage-class=CRawDiskImage -plist \"" + Escape(imagePath) + "\""
		};
		for (int i = 0; i < array.Length; i++)
		{
			if (RunHdiutil(array[i], out string stdout, out string stderr) && TryParsePlist(stdout, out mountPoint, out device))
			{
				return true;
			}
			error = FirstLine(stderr);
		}
		return false;
	}

	public static void Detach(string? device)
	{
		if (!string.IsNullOrWhiteSpace(device) && OperatingSystem.IsMacOS())
		{
			RunHdiutil("detach \"" + Escape(device) + "\" -force", out string _, out string _);
		}
	}

	private static bool RunHdiutil(string args, out string stdout, out string stderr)
	{
		using Process process = Process.Start(new ProcessStartInfo
		{
			FileName = "/usr/bin/hdiutil",
			Arguments = args,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		});
		if (process == null)
		{
			stdout = "";
			stderr = "hdiutil failed to start";
			return false;
		}
		stdout = process.StandardOutput.ReadToEnd();
		stderr = process.StandardError.ReadToEnd();
		process.WaitForExit(60000);
		return process.ExitCode == 0;
	}

	private static bool TryParsePlist(string xml, out string? mountPoint, out string? device)
	{
		mountPoint = null;
		device = null;
		try
		{
			foreach (XElement item in XDocument.Parse(xml).Descendants("dict"))
			{
				List<XElement> list = item.Elements("key").ToList();
				for (int i = 0; i < list.Count; i++)
				{
					string value = list[i].Value;
					XElement xElement = list[i].ElementsAfterSelf().FirstOrDefault();
					if (xElement != null)
					{
						if (value == "mount-point")
						{
							mountPoint = xElement.Value;
						}
						if (value == "dev-entry")
						{
							device = xElement.Value;
						}
					}
				}
			}
			return !string.IsNullOrEmpty(mountPoint);
		}
		catch
		{
			return false;
		}
	}

	private static string Escape(string path)
	{
		return path.Replace("\"", "\\\"");
	}

	private static string FirstLine(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		using StringReader stringReader = new StringReader(text);
		return stringReader.ReadLine()?.Trim() ?? text.Trim();
	}
}
