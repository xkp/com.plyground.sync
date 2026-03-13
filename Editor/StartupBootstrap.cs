#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;

namespace Plysync.Editor
{
	[InitializeOnLoad]
	public static class StartupBootstrap
	{
		private const string SessionKey = "Plysync.StartupBootstrap.Ran";

		static StartupBootstrap()
		{
			EditorApplication.delayCall += TryOpenOnStartup;
		}

		private static void TryOpenOnStartup()
		{
			if (SessionState.GetBool(SessionKey, false)) return;
			SessionState.SetBool(SessionKey, true);

			if (!HasMainSceneUnderPlyground()) return;
			//if (EnvironmentImporter.TryGetMarker(out var marker) && !string.IsNullOrWhiteSpace(marker.gameId)) return;

			var targets = LocalSyncDiscovery.Discover(_ => { });
			if (targets == null || targets.Length == 0) return;

			PlysyncWindow.Open();
		}

		private static bool HasMainSceneUnderPlyground()
		{
			var guids = AssetDatabase.FindAssets("main t:Scene", new[] { "Assets/plyground" });
			if (guids == null || guids.Length == 0) return false;

			for (int i = 0; i < guids.Length; i++)
			{
				var path = AssetDatabase.GUIDToAssetPath(guids[i]);
				if (string.IsNullOrWhiteSpace(path)) continue;
				if (!path.StartsWith("Assets/plyground/", StringComparison.OrdinalIgnoreCase)) continue;
				if (!string.Equals(Path.GetFileNameWithoutExtension(path), "main", StringComparison.OrdinalIgnoreCase)) continue;
				return true;
			}

			return false;
		}
	}
}
#endif
