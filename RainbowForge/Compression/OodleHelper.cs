using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RainbowForge.Compression
{
	public static class OodleHelper
	{
		private static bool _oodleLoaded = false;
		private static readonly object Lock = new();

		public static void EnsureOodleLoaded()
		{
			if (_oodleLoaded)
				return;

			lock (Lock)
			{
				if (_oodleLoaded)
					return;

				var libraryPath = Environment.GetEnvironmentVariable("OODLE2_8_PATH");
				if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
				{
					// Check adjacent to running assembly
					var probe = Path.Combine(AppContext.BaseDirectory, "oo2core_8_win64.dll");
					if (File.Exists(probe))
						libraryPath = probe;
				}

				if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
				{
					// Check Prism release output directory
					var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Prism\bin\Release\net10.0-windows\oo2core_8_win64.dll"));
					if (File.Exists(probe))
						libraryPath = probe;
				}

				if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
				{
					// Check current working directory
					var probe = Path.GetFullPath("oo2core_8_win64.dll");
					if (File.Exists(probe))
						libraryPath = probe;
				}

				if (libraryPath != null && File.Exists(libraryPath))
				{
					NativeLibrary.Load(libraryPath);
					_oodleLoaded = true;
				}
			}
		}
	}
}