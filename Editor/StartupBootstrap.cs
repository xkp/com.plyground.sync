#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using Plyground.Editor;

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
			if (ImportSessionState.TryLoadPendingImportPath(out _))
			{
				PlysyncWindow.ResumePendingImport();
				return;
			}

			if (ImportSessionState.TryLoadPendingPublish(out _, out _, out _))
			{
				PlysyncWindow.ResumePendingPublish();
				return;
			}

			if (SessionState.GetBool(SessionKey, false)) return;
			SessionState.SetBool(SessionKey, true);

			if (HasImportedProject()) return;

			var targets = LocalSyncDiscovery.Discover(_ => { });
			if (targets == null || targets.Length == 0) return;

			PlysyncWindow.Open();
		}

		private static bool HasImportedProject()
		{
			if (EnvironmentImporter.TryGetMarker(out var marker) && !string.IsNullOrWhiteSpace(marker.gameId))
				return true;

			var cache = new CacheStore();
			var lastGameId = cache.LoadLastGameId();
			if (string.IsNullOrWhiteSpace(lastGameId))
				return false;

			return cache.LoadSyncInfo(lastGameId) != null;
		}
	}
}
#endif
