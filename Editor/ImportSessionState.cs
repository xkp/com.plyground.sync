#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	public static class ImportSessionState
	{
		private const string LogKey = "Plysync.LogBuffer";
		private const string PendingImportPathKey = "Plysync.PendingImportPath";

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

		public static void SavePendingImportPath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				ClearPendingImportPath();
				return;
			}

			SessionState.SetString(PendingImportPathKey, path);
		}

		public static bool TryLoadPendingImportPath(out string path)
		{
			path = SessionState.GetString(PendingImportPathKey, "");
			return !string.IsNullOrWhiteSpace(path);
		}

		public static void ClearPendingImportPath()
		{
			SessionState.EraseString(PendingImportPathKey);
		}
	}
}
#endif
