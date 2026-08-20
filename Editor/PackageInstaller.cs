#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
		public enum PackageInstallOutcome
		{
			NoChanges,
			ImportedPackageRequiresReload
		}

		public readonly struct PackageInstallOptions
		{
			public PackageInstallOptions(bool isBrandNewProject, bool useRecordedInstallState)
			{
				IsBrandNewProject = isBrandNewProject;
				UseRecordedInstallState = useRecordedInstallState;
			}

			public bool IsBrandNewProject { get; }
			public bool UseRecordedInstallState { get; }
		}

		[InitializeOnLoad]
		public static class PackageInstaller
		{
			private const string InstalledUnityPackagePrefix = "Plysync.InstalledUnityPackage.";

			static PackageInstaller()
			{
				AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
				AssetDatabase.importPackageFailed += OnImportPackageFailed;
				AssetDatabase.importPackageCancelled += OnImportPackageCancelled;
			}

			public static async Task<PackageInstallOutcome> Install(PackagesBlock pkgs, Action<string> log, CancellationToken ct, PackageInstallOptions options = default)
			{
				if (pkgs == null)
				{
					log("No packages block provided.");
					return PackageInstallOutcome.NoChanges;
				}

				SortInPlace(pkgs);
				log($"Package installer received {pkgs.value?.Length ?? 0} package path(s).");

				FinalizePendingPackageImport(log);

				//if (pkgs.upm != null && pkgs.upm.Length > 0)
				//{
				//	changed |= await InstallUpmPackages(pkgs.upm, log, ct);
			//}
				if (pkgs.value != null && pkgs.value.Length > 0)
				{
					var importedPackage = await InstallUnityPackages(pkgs.value, log, ct, options);
					if (importedPackage)
						return PackageInstallOutcome.ImportedPackageRequiresReload;
				}

				await RebuildTypes(log, ct);

				log("Package install found no changes.");
				return PackageInstallOutcome.NoChanges;
			}

		private static void SortInPlace(PackagesBlock pkgs)
		{
			if (pkgs == null) return;

			if (pkgs.value != null)
			{
				pkgs.value = pkgs.value
					.Where(p => !string.IsNullOrWhiteSpace(p))
					.Select(p => p.Trim())
					.Distinct(StringComparer.OrdinalIgnoreCase)
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

		private static Task<bool> InstallUnityPackages(string[] packages, Action<string> log, CancellationToken ct, PackageInstallOptions options)
		{
				return InstallUnityPackagesAsync(packages, log, ct, options);
			}

			private static async Task<bool> InstallUnityPackagesAsync(string[] packages, Action<string> log, CancellationToken ct, PackageInstallOptions options)
			{
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
					var canSkipFromRecordedInstall = options.UseRecordedInstallState && !options.IsBrandNewProject;

					if (ImportSessionState.HasInstalledPackageIdentity(identity))
					{
						log($".unitypackage already imported in this install sequence: {Path.GetFileName(pkg)}");
						continue;
					}

					if (canSkipFromRecordedInstall &&
						!string.IsNullOrWhiteSpace(fingerprint) &&
						string.Equals(installedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
					{
						log($".unitypackage already imported: {Path.GetFileName(pkg)}");
						continue;
					}

					if (options.IsBrandNewProject && !string.IsNullOrWhiteSpace(installedFingerprint))
					{
						log($".unitypackage recorded state ignored for brand-new project: {Path.GetFileName(pkg)}");
					}
					else if (!string.IsNullOrWhiteSpace(fingerprint) &&
						string.Equals(installedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
					{
						log($".unitypackage import record was stale, so it will be re-imported: {Path.GetFileName(pkg)}");
						EditorPrefs.DeleteKey(installedKey);
					}

					var localPath = pkg; // await ResolveUnityPackageFilePath(pkg, log, ct);
					log($"Importing .unitypackage: {Path.GetFileName(localPath)}");
					ImportSessionState.SavePendingPackageImport(localPath, fingerprint);
					AssetDatabase.ImportPackage(localPath, false);
					log($"Queued .unitypackage import and recorded pending resume state: {localPath}");
					return true;
				}

				return false;
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

		private static void FinalizePendingPackageImport(Action<string> log)
		{
			if (!ImportSessionState.TryLoadPendingPackageImport(out var packagePath, out var fingerprint))
				return;

			if (!string.IsNullOrWhiteSpace(packagePath))
			{
				log?.Invoke($"Finalizing previously imported package after Unity reload: {Path.GetFileName(packagePath)}");
				var identity = GetUnityPackageIdentity(packagePath);
				var installedKey = GetUnityPackageInstalledKey(identity);
				if (!string.IsNullOrWhiteSpace(fingerprint))
					EditorPrefs.SetString(installedKey, fingerprint);
				ImportSessionState.MarkInstalledPackageIdentity(identity);
			}

			ImportSessionState.ClearPendingPackageImport();
			AssetDatabase.Refresh();
		}

		private static void OnImportPackageCompleted(string packageName)
		{
			if (PlygroundBuildScript.HasPendingHeadlessBuild())
			{
				EditorApplication.delayCall += PlygroundBuildScript.ResumePendingBuild;
				return;
			}

			if (ImportSessionState.TryLoadPendingImportPath(out _))
				EditorApplication.delayCall += PlysyncWindow.ResumePendingImport;
		}

		private static void OnImportPackageFailed(string packageName, string error)
		{
			ImportSessionState.ClearPendingPackageImport();
			ImportSessionState.ClearPackageInstallSequencePath();
			Debug.LogError($"Failed importing Unity package '{packageName}': {error}");
		}

		private static void OnImportPackageCancelled(string packageName)
		{
			ImportSessionState.ClearPendingPackageImport();
			ImportSessionState.ClearPackageInstallSequencePath();
			Debug.LogWarning($"Cancelled Unity package import: {packageName}");
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

		private static bool HasImportedUnityPackageContent(string packagePath, Action<string> log)
		{
			try
			{
				var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
				if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
					return true;

				var knownFilePaths = EnumerateUnityPackageAssetPaths(packagePath)
					.Where(IsRelevantUnityPackageFilePath)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray();
				if (knownFilePaths.Length == 0)
				{
					log?.Invoke($"Could not verify concrete asset files for {Path.GetFileName(packagePath)} from the package archive. Falling back to the recorded import state.");
					return true;
				}

				var existingFileCount = 0;
				foreach (var relativePath in knownFilePaths)
				{
					var absolutePath = ToProjectAbsolutePath(projectRoot, relativePath);
					if (string.IsNullOrWhiteSpace(absolutePath))
						continue;

					if (File.Exists(absolutePath))
					{
						existingFileCount++;
						if (existingFileCount >= GetRequiredInstalledFileCount(knownFilePaths.Length))
							return true;
					}
				}

				return false;
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed verifying imported contents for {Path.GetFileName(packagePath)}: {ex.Message}. Falling back to the recorded import state.");
				return true;
			}
		}

		private static IEnumerable<string> EnumerateUnityPackageAssetPaths(string packagePath)
		{
			using var fileStream = File.OpenRead(packagePath);
			using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);

			var header = new byte[512];
			while (TryReadTarBlock(gzipStream, header))
			{
				if (IsZeroBlock(header))
					yield break;

				var entryName = ReadTarString(header, 0, 100);
				var size = ReadTarOctal(header, 124, 12);
				var dataSize = AlignTarSize(size);

				if (size < 0)
					yield break;

				if (string.Equals(Path.GetFileName(entryName), "pathname", StringComparison.OrdinalIgnoreCase) && size > 0)
				{
					var content = ReadTarEntryString(gzipStream, size, dataSize);
					var relativePath = NormalizeUnityPackageAssetPath(content);
					if (!string.IsNullOrWhiteSpace(relativePath))
						yield return relativePath;
				}
				else
				{
					SkipTarEntry(gzipStream, dataSize);
				}
			}
		}

		private static string NormalizeUnityPackageAssetPath(string rawPath)
		{
			if (string.IsNullOrWhiteSpace(rawPath))
				return null;

			var normalized = rawPath.Trim().Replace('\\', '/');
			if (normalized.StartsWith("./", StringComparison.Ordinal))
				normalized = normalized.Substring(2);

			if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
				normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
				normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
				return normalized;

			return null;
		}

		private static bool IsRelevantUnityPackageFilePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return false;

			if (path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
				return false;

			return LooksLikeFilePath(path);
		}

		private static bool LooksLikeFilePath(string path)
		{
			var fileName = Path.GetFileName(path);
			return !string.IsNullOrWhiteSpace(Path.GetExtension(fileName));
		}

		private static int GetRequiredInstalledFileCount(int totalFileCount)
		{
			if (totalFileCount <= 0)
				return 1;

			if (totalFileCount <= 3)
				return totalFileCount;

			return Math.Min(3, totalFileCount);
		}

		private static string ToProjectAbsolutePath(string projectRoot, string relativePath)
		{
			if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(relativePath))
				return null;

			try
			{
				var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
				return fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
			}
			catch
			{
				return null;
			}
		}

		private static bool TryReadTarBlock(Stream stream, byte[] buffer)
		{
			var offset = 0;
			while (offset < buffer.Length)
			{
				var read = stream.Read(buffer, offset, buffer.Length - offset);
				if (read <= 0)
					return offset > 0 ? throw new EndOfStreamException("Unexpected end of tar stream.") : false;

				offset += read;
			}

			return true;
		}

		private static bool IsZeroBlock(byte[] buffer)
		{
			for (var i = 0; i < buffer.Length; i++)
			{
				if (buffer[i] != 0)
					return false;
			}

			return true;
		}

		private static string ReadTarString(byte[] buffer, int offset, int count)
		{
			var value = Encoding.UTF8.GetString(buffer, offset, count);
			var terminator = value.IndexOf('\0');
			if (terminator >= 0)
				value = value.Substring(0, terminator);

			return value.Trim();
		}

		private static long ReadTarOctal(byte[] buffer, int offset, int count)
		{
			var value = ReadTarString(buffer, offset, count).Trim();
			if (string.IsNullOrWhiteSpace(value))
				return 0;

			try
			{
				return Convert.ToInt64(value, 8);
			}
			catch
			{
				return -1;
			}
		}

		private static long AlignTarSize(long size)
		{
			const int blockSize = 512;
			return ((size + blockSize - 1) / blockSize) * blockSize;
		}

		private static string ReadTarEntryString(Stream stream, long size, long paddedSize)
		{
			var bytes = new byte[paddedSize];
			ReadExact(stream, bytes, 0, bytes.Length);
			return Encoding.UTF8.GetString(bytes, 0, (int)size);
		}

		private static void SkipTarEntry(Stream stream, long paddedSize)
		{
			if (paddedSize <= 0)
				return;

			var buffer = new byte[4096];
			var remaining = paddedSize;
			while (remaining > 0)
			{
				var toRead = (int)Math.Min(buffer.Length, remaining);
				var read = stream.Read(buffer, 0, toRead);
				if (read <= 0)
					throw new EndOfStreamException("Unexpected end of tar stream while skipping entry.");

				remaining -= read;
			}
		}

		private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
		{
			var totalRead = 0;
			while (totalRead < count)
			{
				var read = stream.Read(buffer, offset + totalRead, count - totalRead);
				if (read <= 0)
					throw new EndOfStreamException("Unexpected end of stream.");

				totalRead += read;
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
