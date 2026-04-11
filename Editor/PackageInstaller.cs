#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

	namespace Plysync.Editor
	{
		public static class PackageInstaller
		{
			private const string InstalledUnityPackagePrefix = "Plysync.InstalledUnityPackage.";

			public static async Task<bool> Install(PackagesBlock pkgs, Action<string> log, CancellationToken ct)
			{
				if (pkgs == null)
				{
					log("No packages block provided.");
					return false;
				}

				SortInPlace(pkgs);
				var changed = false;
				log($"Package installer received {pkgs.value?.Length ?? 0} package path(s).");

				//if (pkgs.upm != null && pkgs.upm.Length > 0)
				//{
				//	changed |= await InstallUpmPackages(pkgs.upm, log, ct);
			//}
				if (pkgs.value != null && pkgs.value.Length > 0)
				{
					changed |= await InstallUnityPackages(pkgs.value, log, ct);
				}

				if (changed)
				{
					await RebuildTypes(log, ct);
				}

				log(changed ? "Package install changed the project." : "Package install found no changes.");
				return changed;
			}

		private static void SortInPlace(PackagesBlock pkgs)
		{
			if (pkgs == null) return;

			if (pkgs.value != null)
			{
				pkgs.value = pkgs.value
					.Where(p => p != null)
					.OrderBy(p => p.Contains("plyground"))
					.ToArray();
			}
		}

		private static async Task<bool> InstallUpmPackages(UpmPackage[] packages, Action<string> log, CancellationToken ct)
		{
			var listRequest = UnityEditor.PackageManager.Client.List(true);
			await WaitForRequest(listRequest, ct);
			var changed = false;

			var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (listRequest.Result != null)
			{
				foreach (var p in listRequest.Result)
				{
					if (!string.IsNullOrWhiteSpace(p?.name))
						installed.Add(p.name);
				}
			}

			foreach (var pkg in packages)
			{
				ct.ThrowIfCancellationRequested();
				if (pkg == null) continue;

				var display = pkg.name ?? pkg.git ?? "(unknown)";

				if (!string.IsNullOrWhiteSpace(pkg.name) && installed.Contains(pkg.name))
				{
					log($"UPM already installed: {pkg.name}");
					continue;
				}

				var installTarget = BuildUpmInstallTarget(pkg);
				if (string.IsNullOrWhiteSpace(installTarget))
				{
					log($"Skipping invalid UPM package entry: {display}");
					continue;
				}

				log($"Installing UPM package: {installTarget}");
				var addRequest = UnityEditor.PackageManager.Client.Add(installTarget);
				await WaitForRequest(addRequest, ct);
				changed = true;

				if (!string.IsNullOrWhiteSpace(pkg.name))
					installed.Add(pkg.name);
			}

			return changed;
		}

		private static Task<bool> InstallUnityPackages(string[] packages, Action<string> log, CancellationToken ct)
		{
				return InstallUnityPackagesAsync(packages, log, ct);
			}

			private static async Task<bool> InstallUnityPackagesAsync(string[] packages, Action<string> log, CancellationToken ct)
			{
				var changed = false;
				foreach (var pkg in packages)
				{
					ct.ThrowIfCancellationRequested();
					if (pkg == null) continue;
					if (!File.Exists(pkg))
						throw new FileNotFoundException("Unity package file was not found.", pkg);

					var identity = GetUnityPackageIdentity(pkg);
					var fingerprint = GetUnityPackageFingerprint(pkg);
					var installedKey = GetUnityPackageInstalledKey(identity);
					var installedFingerprint = EditorPrefs.GetString(installedKey, "");

					if (!string.IsNullOrWhiteSpace(fingerprint) &&
						string.Equals(installedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
					{
						log($".unitypackage already imported: {Path.GetFileName(pkg)}");
						continue;
					}

					var localPath = pkg; // await ResolveUnityPackageFilePath(pkg, log, ct);
					log($"Importing .unitypackage: {Path.GetFileName(localPath)}");
					await ImportUnityPackageAndWait(localPath, log, ct);
					log($"Imported .unitypackage from: {localPath}");
					if (!string.IsNullOrWhiteSpace(fingerprint))
						EditorPrefs.SetString(installedKey, fingerprint);
					changed = true;
				}

				return changed;
			}

			private static async Task ImportUnityPackageAndWait(string localPath, Action<string> log, CancellationToken ct)
			{
				var packageFileName = Path.GetFileName(localPath);
				var completion = new TaskCompletionSource<PackageImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
				var eventSeen = false;

				void OnCompleted(string packageName)
				{
					eventSeen = true;
					log($"Unity package import completed: {packageName}");
					completion.TrySetResult(new PackageImportResult(packageName, PackageImportStatus.Completed));
				}

				void OnFailed(string packageName, string error)
				{
					eventSeen = true;
					completion.TrySetResult(new PackageImportResult(packageName, PackageImportStatus.Failed, error));
				}

				void OnCancelled(string packageName)
				{
					eventSeen = true;
					completion.TrySetResult(new PackageImportResult(packageName, PackageImportStatus.Cancelled));
				}

				AssetDatabase.importPackageCompleted += OnCompleted;
				AssetDatabase.importPackageFailed += OnFailed;
				AssetDatabase.importPackageCancelled += OnCancelled;

				try
				{
					AssetDatabase.ImportPackage(localPath, false);
					log($"Waiting for Unity package import callbacks: {packageFileName}");

					while (!completion.Task.IsCompleted)
					{
						ct.ThrowIfCancellationRequested();
						await Task.Delay(100, ct);
					}

					var result = await completion.Task;
					if (result.status == PackageImportStatus.Failed)
						throw new Exception($"Unity package import failed for '{packageFileName}': {result.error ?? "Unknown error."}");

					if (result.status == PackageImportStatus.Cancelled)
						throw new OperationCanceledException($"Unity package import was cancelled for '{packageFileName}'.");

					AssetDatabase.Refresh();
				}
				finally
				{
					AssetDatabase.importPackageCompleted -= OnCompleted;
					AssetDatabase.importPackageFailed -= OnFailed;
					AssetDatabase.importPackageCancelled -= OnCancelled;

					if (!eventSeen)
						log($"Unity package import callbacks were detached without an explicit completion event for: {packageFileName}");
				}
			}

			private readonly struct PackageImportResult
			{
				public readonly string packageName;
				public readonly PackageImportStatus status;
				public readonly string error;

				public PackageImportResult(string packageName, PackageImportStatus status, string error = null)
				{
					this.packageName = packageName;
					this.status = status;
					this.error = error;
				}
			}

			private enum PackageImportStatus
			{
				Completed,
				Failed,
				Cancelled
			}

			private static async Task RebuildTypes(Action<string> log, CancellationToken ct)
			{
				log("Rebuilding types after package install...");
				AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

				await WaitForEditorToSettle(log, ct);

				// Build a name->Type map so later Type.GetType calls are likely to hit warm metadata.
				var allTypes = TypeCache.GetTypesDerivedFrom<object>();
				var map = new Dictionary<string, Type>(StringComparer.Ordinal);
				foreach (var t in allTypes)
				{
					ct.ThrowIfCancellationRequested();
					if (t == null || string.IsNullOrWhiteSpace(t.FullName)) continue;
					map[t.FullName] = t;
					map[t.Name] = t;
				}

				log($"Type rebuild complete. Cached {map.Count} names.");
			}

		private static async Task WaitForEditorToSettle(Action<string> log, CancellationToken ct)
		{
			var loggedWait = false;
			while (EditorApplication.isUpdating || EditorApplication.isCompiling)
			{
				ct.ThrowIfCancellationRequested();
				if (!loggedWait)
				{
					log("Waiting for Unity to finish asset updates/script compilation after importing packages...");
					loggedWait = true;
				}

				await Task.Delay(100, ct);
			}
		}

		private static bool TryResolveExistingLocalPath(string source, out string localPath)
		{
			localPath = null;
			if (string.IsNullOrWhiteSpace(source)) return false;

			// file:///C:/... -> local path
			if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
			{
				var p = uri.LocalPath;
				if (File.Exists(p))
				{
					localPath = p;
					return true;
				}
			}

			// Direct OS path (absolute, UNC, or relative to current process working dir).
			if (File.Exists(source))
			{
				localPath = Path.GetFullPath(source);
				return true;
			}

			return false;
		}

		private static bool IsHttpUrl(string source)
		{
			if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
			return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
		}

		private static string NormalizeSha(string sha)
		{
			if (string.IsNullOrWhiteSpace(sha)) return "";
			sha = sha.Trim();
			return sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
				? sha.Substring("sha256:".Length)
				: sha;
		}

		private static string MakeSafeFileName(string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return "pkg";
			var invalid = Path.GetInvalidFileNameChars();
			var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
			var s = new string(chars);
			return string.IsNullOrWhiteSpace(s) ? "pkg" : s;
		}

		private static string BuildUpmInstallTarget(UpmPackage pkg)
		{
			if (pkg == null) return null;
			if (!string.IsNullOrWhiteSpace(pkg.git)) return pkg.git;
			if (string.IsNullOrWhiteSpace(pkg.name)) return null;
			if (string.IsNullOrWhiteSpace(pkg.version)) return pkg.name;
			return $"{pkg.name}@{pkg.version}";
		}

		private static string GetUnityPackageIdentity(string packagePath)
		{
			if (string.IsNullOrWhiteSpace(packagePath))
				return "unknown";

			var projectKey = GetCurrentProjectKey();
			var fullPackagePath = Path.GetFullPath(packagePath);
			return MakeSafeFileName(projectKey + "_" + fullPackagePath);
		}

		private static string GetUnityPackageInstalledKey(string identity)
		{
			return InstalledUnityPackagePrefix + MakeSafeFileName(identity);
		}

		private static string GetUnityPackageFingerprint(string packagePath)
		{
			if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
				return "";

			var info = new FileInfo(packagePath);
			return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
		}

		private static string GetCurrentProjectKey()
		{
			try
			{
				var assetsPath = Application.dataPath;
				if (string.IsNullOrWhiteSpace(assetsPath))
					return "unknown-project";

				var projectRoot = Directory.GetParent(assetsPath)?.FullName ?? assetsPath;
				return projectRoot;
			}
			catch
			{
				return "unknown-project";
			}
		}

		private static async Task WaitForRequest(Request request, CancellationToken ct)
		{
			while (!request.IsCompleted)
			{
				ct.ThrowIfCancellationRequested();
				await Task.Delay(100, ct);
			}

			if (request.Status == StatusCode.Failure)
				throw new Exception(request.Error?.message ?? "Unity Package Manager request failed.");
		}
	}
}
#endif
