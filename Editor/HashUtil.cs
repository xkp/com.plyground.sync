#if UNITY_EDITOR
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Plysync.Editor
{
	public static class HashUtil
	{
		public static string Sha256File(string path)
		{
			using var sha = SHA256.Create();
			using var fs = File.OpenRead(path);
			var hash = sha.ComputeHash(fs);
			var sb = new StringBuilder(hash.Length * 2);
			foreach (var b in hash) sb.Append(b.ToString("x2"));
			return "sha256:" + sb.ToString();
		}
	}
}
#endif