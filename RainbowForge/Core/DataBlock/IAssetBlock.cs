using System.IO;

namespace RainbowForge.Core.DataBlock
{
	/// <summary>
	///     No-op class to serve as a common parent for all ForgeAsset subcomponents
	/// </summary>
	public interface IAssetBlock
	{
		public Stream GetDataStream(BinaryReader r);

		/// <summary>
		/// Reads at most the requested unpacked prefix. Header inspection should use
		/// this instead of materializing every chunk in a potentially huge asset.
		/// </summary>
		public Stream GetDataPrefixStream(BinaryReader r, int maxBytes);
	}
}
