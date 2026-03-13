#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	[Serializable]
	public class PlysyncCache
	{
		public string gameId;              // we use SyncBuildInfo.path as "gameId"
		public string lastImportedRevision; // can be "unknown"
		public string lastImportedAtUtc;
	}

	public sealed class CacheStore
	{
		private static string DirPath => Path.Combine("ProjectSettings", "Plysync");
		private static string CachePathFor(string gameId) => Path.Combine(DirPath, $"{Sanitize(gameId)}.cache.json");

		// NEW: store the SyncBuildInfo (paths returned by /sync/list)
		private static string SyncInfoPathFor(string gameId) => Path.Combine(DirPath, $"{Sanitize(gameId)}.syncinfo.json");

		private static string LastGamePath => Path.Combine(DirPath, "_last_game.txt");

		public PlysyncCache Read(string gameId)
		{
			try
			{
				var p = CachePathFor(gameId);
				if (!File.Exists(p)) return null;
				return JsonUtility.FromJson<PlysyncCache>(File.ReadAllText(p));
			}
			catch { return null; }
		}

		public void Write(PlysyncCache cache)
		{
			Directory.CreateDirectory(DirPath);
			File.WriteAllText(CachePathFor(cache.gameId), JsonUtility.ToJson(cache, true));
			File.WriteAllText(LastGamePath, cache.gameId ?? "");
			AssetDatabase.Refresh();
		}

		public void Delete(string gameId)
		{
			var p1 = CachePathFor(gameId);
			var p2 = SyncInfoPathFor(gameId);

			if (File.Exists(p1)) File.Delete(p1);
			if (File.Exists(p2)) File.Delete(p2);

			AssetDatabase.Refresh();
		}

		// ---------------------------
		// NEW: SyncBuildInfo storage
		// ---------------------------

		public void SaveSyncInfo(SyncBuildInfo info)
		{
			if (info == null) return;
			if (string.IsNullOrWhiteSpace(info.path)) return;

			Directory.CreateDirectory(DirPath);

			var json = JsonUtility.ToJson(info, true);
			File.WriteAllText(SyncInfoPathFor(info.path), json);
			File.WriteAllText(LastGamePath, info.path ?? "");

			AssetDatabase.Refresh();
		}

		public SyncBuildInfo LoadSyncInfo(string gameId)
		{
			try
			{
				var p = SyncInfoPathFor(gameId);
				if (!File.Exists(p)) return null;
				return JsonUtility.FromJson<SyncBuildInfo>(File.ReadAllText(p));
			}
			catch { return null; }
		}

		public string LoadLastGameId()
		{
			try
			{
				if (!File.Exists(LastGamePath)) return null;
				var s = File.ReadAllText(LastGamePath).Trim();
				return string.IsNullOrWhiteSpace(s) ? null : s;
			}
			catch { return null; }
		}

		// ---------------------------
		// Helpers
		// ---------------------------

		// Because your "gameId" is a path, we must make it file-name-safe.
		private static string Sanitize(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "unknown";
			foreach (var c in Path.GetInvalidFileNameChars())
				s = s.Replace(c, '_');
			// also replace slashes/backslashes because path strings often contain them
			s = s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
			return s;
		}
	}
}
#endif