using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RainbowForge.Compression;

namespace Prism
{
	internal static class Program
	{
		/// <summary>
		///     The main entry point for the application.
		/// </summary>
		[STAThread]
		private static void Main(string[] args)
		{
			var adjacentOodle = Path.Combine(AppContext.BaseDirectory, "oo2core_8_win64.dll");
			if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OODLE2_8_PATH")) && File.Exists(adjacentOodle))
				Environment.SetEnvironmentVariable("OODLE2_8_PATH", adjacentOodle);
			if (File.Exists(Environment.GetEnvironmentVariable("OODLE2_8_PATH")))
				OodleHelper.EnsureOodleLoaded();

			if (args.Length > 0 && args[0] == "--export-scene")
			{
				Environment.ExitCode = SceneExporter.Run(args);
				return;
			}

			Application.SetHighDpiMode(HighDpiMode.SystemAware);
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new PrismForm(args));
		}
	}
}
