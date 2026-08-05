#if UNITY_EDITOR
using System.Linq;
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
		private const string PackageInstallSequencePathKey = "Plysync.PackageInstallSequence.Path";
		private const string InstalledPackageIdentitiesKey = "Plysync.PackageInstallSequence.Installed";
		private const string PendingPublishGameIdKey = "Plysync.PendingPublish.GameId";
		private const string PendingPublishVariationIdKey = "Plysync.PendingPublish.VariationId";
		private const string PendingPublishRevisionKey = "Plysync.PendingPublish.Revision";
		private const string PendingPublishForceContinueKey = "Plysync.PendingPublish.ForceContinue";

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

		public static void SavePackageInstallSequencePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				ClearPackageInstallSequencePath();
				return;
			}

			var currentPath = SessionState.GetString(PackageInstallSequencePathKey, "");
			if (!string.IsNullOrWhiteSpace(currentPath) &&
				!string.Equals(currentPath, path, System.StringComparison.OrdinalIgnoreCase))
			{
				ClearInstalledPackageIdentities();
			}

			SessionState.SetString(PackageInstallSequencePathKey, path);
		}

		public static bool HasPackageInstallSequencePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return false;

			var currentPath = SessionState.GetString(PackageInstallSequencePathKey, "");
			return !string.IsNullOrWhiteSpace(currentPath) &&
				string.Equals(currentPath, path, System.StringComparison.OrdinalIgnoreCase);
		}

		public static void ClearPackageInstallSequencePath()
		{
			SessionState.EraseString(PackageInstallSequencePathKey);
			ClearInstalledPackageIdentities();
		}

		public static void MarkInstalledPackageIdentity(string identity)
		{
			if (string.IsNullOrWhiteSpace(identity))
				return;

			var identities = LoadInstalledPackageIdentities();
			if (identities.Add(identity.Trim()))
				SaveInstalledPackageIdentities(identities);
		}

		public static bool HasInstalledPackageIdentity(string identity)
		{
			if (string.IsNullOrWhiteSpace(identity))
				return false;

			return LoadInstalledPackageIdentities().Contains(identity.Trim());
		}

		public static void ClearInstalledPackageIdentities()
		{
			SessionState.EraseString(InstalledPackageIdentitiesKey);
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

		public static void SavePendingPublish(string gameId, string variationId, string revision)
		{
			if (string.IsNullOrWhiteSpace(gameId))
			{
				ClearPendingPublish();
				return;
			}

			SessionState.SetString(PendingPublishGameIdKey, gameId);
			SessionState.SetString(PendingPublishVariationIdKey, variationId ?? "");
			SessionState.SetString(PendingPublishRevisionKey, revision ?? "");
		}

		public static bool TryLoadPendingPublish(out string gameId, out string variationId, out string revision)
		{
			gameId = SessionState.GetString(PendingPublishGameIdKey, "");
			variationId = SessionState.GetString(PendingPublishVariationIdKey, "");
			revision = SessionState.GetString(PendingPublishRevisionKey, "");
			return !string.IsNullOrWhiteSpace(gameId);
		}

		public static void ClearPendingPublish()
		{
			SessionState.EraseString(PendingPublishGameIdKey);
			SessionState.EraseString(PendingPublishVariationIdKey);
			SessionState.EraseString(PendingPublishRevisionKey);
			SessionState.EraseBool(PendingPublishForceContinueKey);
		}

		public static void RequestPendingPublishForceContinue()
		{
			SessionState.SetBool(PendingPublishForceContinueKey, true);
		}

		public static bool ConsumePendingPublishForceContinue()
		{
			var value = SessionState.GetBool(PendingPublishForceContinueKey, false);
			if (value)
				SessionState.EraseBool(PendingPublishForceContinueKey);

			return value;
		}

		private static System.Collections.Generic.HashSet<string> LoadInstalledPackageIdentities()
		{
			var raw = SessionState.GetString(InstalledPackageIdentitiesKey, "");
			var result = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(raw))
				return result;

			foreach (var value in raw.Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
			{
				var trimmed = value.Trim();
				if (!string.IsNullOrWhiteSpace(trimmed))
					result.Add(trimmed);
			}

			return result;
		}

		private static void SaveInstalledPackageIdentities(System.Collections.Generic.HashSet<string> identities)
		{
			if (identities == null || identities.Count == 0)
			{
				SessionState.EraseString(InstalledPackageIdentitiesKey);
				return;
			}

			SessionState.SetString(InstalledPackageIdentitiesKey, string.Join("\n", identities.OrderBy(x => x, System.StringComparer.OrdinalIgnoreCase)));
		}
	}
}
#endif
