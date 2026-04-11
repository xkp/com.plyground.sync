#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	public static class ImportSessionState
	{
		private const string LogKey = "Plysync.LogBuffer";
		private const string PendingImportPathKey = "Plysync.PendingImportPath";
		private const string PendingPackagePathKey = "Plysync.PendingPackage.Path";
		private const string PendingPackageFingerprintKey = "Plysync.PendingPackage.Fingerprint";

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

		public static void SavePendingPackageImport(string packagePath, string fingerprint)
		{
			if (string.IsNullOrWhiteSpace(packagePath))
			{
				ClearPendingPackageImport();
				return;
			}

			SessionState.SetString(PendingPackagePathKey, packagePath);
			SessionState.SetString(PendingPackageFingerprintKey, fingerprint ?? "");
		}

		public static bool TryLoadPendingPackageImport(out string packagePath, out string fingerprint)
		{
			packagePath = SessionState.GetString(PendingPackagePathKey, "");
			fingerprint = SessionState.GetString(PendingPackageFingerprintKey, "");
			return !string.IsNullOrWhiteSpace(packagePath);
		}

		public static void ClearPendingPackageImport()
		{
			SessionState.EraseString(PendingPackagePathKey);
			SessionState.EraseString(PendingPackageFingerprintKey);
		}
	}
}
#endif
