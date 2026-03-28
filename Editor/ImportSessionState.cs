#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	public static class ImportSessionState
	{
		private const string LogKey = "Plysync.LogBuffer";
		private const string PendingImportKey = "Plysync.PendingImport";

		public static string LoadLog()
		{
			return SessionState.GetString(LogKey, "");
		}

		public static void SaveLog(string text)
		{
			SessionState.SetString(LogKey, text ?? "");
		}

		public static void ClearLog()
		{
			SessionState.EraseString(LogKey);
		}

		public static void SavePendingImport(SyncBuildInfo info)
		{
			if (info == null)
			{
				ClearPendingImport();
				return;
			}

			SessionState.SetString(PendingImportKey, JsonUtility.ToJson(info));
		}

		public static bool TryLoadPendingImport(out SyncBuildInfo info)
		{
			info = null;
			var json = SessionState.GetString(PendingImportKey, "");
			if (string.IsNullOrWhiteSpace(json))
				return false;

			try
			{
				info = JsonUtility.FromJson<SyncBuildInfo>(json);
				return info != null && !string.IsNullOrWhiteSpace(info.path);
			}
			catch
			{
				return false;
			}
		}

		public static void ClearPendingImport()
		{
			SessionState.EraseString(PendingImportKey);
		}
	}
}
#endif
