#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Plysync.Editor
{
	public sealed class PlysyncClient
	{
		private readonly Action<string> _log;

		public PlysyncClient(Action<string> log)
		{
			_log = log ?? (_ => { });
		}

		public PackagesBlock ResolvePackagesForModules(string[] moduleIds, CancellationToken ct)
		{
			if (moduleIds == null || moduleIds.Length == 0)
				return null;

			var resolvedPaths = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var modulesRoot = GetLocalModulesRoot();
			if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot))
			{
				_log($"Local module cache folder not found: {modulesRoot}");
				return null;
			}

			foreach (var rawModuleId in moduleIds)
			{
				ct.ThrowIfCancellationRequested();

				var moduleId = (rawModuleId ?? "").Trim();
				if (string.IsNullOrWhiteSpace(moduleId))
					continue;

				var moduleFolder = Path.Combine(modulesRoot, moduleId);
				if (!Directory.Exists(moduleFolder))
				{
					_log($"Module folder not found for '{moduleId}': {moduleFolder}");
					continue;
				}

				var bgmPath = Path.Combine(moduleFolder, "module.bgm");
				if (!File.Exists(bgmPath))
				{
					_log($"module.bgm not found for module '{moduleId}': {bgmPath}");
					continue;
				}

				ModuleBgm bgm;
				try
				{
					var json = File.ReadAllText(bgmPath);
					bgm = JsonUtility.FromJson<ModuleBgm>(json);
				}
				catch (Exception ex)
				{
					_log($"Failed to parse module.bgm for module '{moduleId}': {ex.Message}");
					continue;
				}

				if (bgm?.packages == null || bgm.packages.Length == 0)
				{
					_log($"No packages declared in module.bgm for module '{moduleId}'.");
					continue;
				}

				var packagesFolder = Path.Combine(moduleFolder, "Packages");
				if (!Directory.Exists(packagesFolder))
				{
					_log($"Packages folder not found for module '{moduleId}': {packagesFolder}");
					continue;
				}

				foreach (var pkg in bgm.packages)
				{
					ct.ThrowIfCancellationRequested();

					var packageFileName = (pkg?.fileName ?? "").Trim();
					if (string.IsNullOrWhiteSpace(packageFileName))
					{
						_log($"Skipped package entry with empty fileName in module '{moduleId}'.");
						continue;
					}

					var resolved = ResolvePackageFile(packagesFolder, packageFileName);
					if (string.IsNullOrWhiteSpace(resolved))
					{
						_log($"Package file not found for module '{moduleId}', fileName '{packageFileName}' in '{packagesFolder}'.");
						continue;
					}

					if (seen.Add(resolved))
					{
						resolvedPaths.Add(resolved);
						_log($"Resolved package for module '{moduleId}': {resolved}");
					}
				}
			}

			if (resolvedPaths.Count == 0)
			{
				_log("Local package resolve returned no installable packages.");
				return null;
			}

			return new PackagesBlock
			{
				value = resolvedPaths.ToArray()
			};
		}

		public string[] ResolveFilesToRemoveForModules(string[] moduleIds, CancellationToken ct)
		{
			if (moduleIds == null || moduleIds.Length == 0)
				return Array.Empty<string>();

			var filesToRemove = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var modulesRoot = GetLocalModulesRoot();
			if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot))
			{
				_log($"Local module cache folder not found: {modulesRoot}");
				return Array.Empty<string>();
			}

			foreach (var bgm in EnumerateModuleBgms(moduleIds, modulesRoot, ct))
			{
				if (bgm?.filesToRemove == null || bgm.filesToRemove.Length == 0)
					continue;

				foreach (var rawPath in bgm.filesToRemove)
				{
					ct.ThrowIfCancellationRequested();

					var relativePath = (rawPath ?? "").Trim();
					if (string.IsNullOrWhiteSpace(relativePath))
						continue;

					if (seen.Add(relativePath))
						filesToRemove.Add(relativePath);
				}
			}

			return filesToRemove.ToArray();
		}

		private IEnumerable<ModuleBgm> EnumerateModuleBgms(string[] moduleIds, string modulesRoot, CancellationToken ct)
		{
			foreach (var rawModuleId in moduleIds)
			{
				ct.ThrowIfCancellationRequested();

				var moduleId = (rawModuleId ?? "").Trim();
				if (string.IsNullOrWhiteSpace(moduleId))
					continue;

				var moduleFolder = Path.Combine(modulesRoot, moduleId);
				if (!Directory.Exists(moduleFolder))
				{
					_log($"Module folder not found for '{moduleId}': {moduleFolder}");
					continue;
				}

				var bgmPath = Path.Combine(moduleFolder, "module.bgm");
				if (!File.Exists(bgmPath))
				{
					_log($"module.bgm not found for module '{moduleId}': {bgmPath}");
					continue;
				}

				ModuleBgm bgm;
				try
				{
					var json = File.ReadAllText(bgmPath);
					bgm = JsonUtility.FromJson<ModuleBgm>(json);
				}
				catch (Exception ex)
				{
					_log($"Failed to parse module.bgm for module '{moduleId}': {ex.Message}");
					continue;
				}

				yield return bgm;
			}
		}

		private static string GetLocalModulesRoot()
		{
			var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Combine(userFolder, "threedee", "cache", "modules");
		}

		private static string ResolvePackageFile(string packagesFolder, string fileNameFromBgm)
		{
			if (string.IsNullOrWhiteSpace(packagesFolder) || string.IsNullOrWhiteSpace(fileNameFromBgm))
				return null;

			// 1) Exact direct match
			var direct = Path.Combine(packagesFolder, fileNameFromBgm);
			if (File.Exists(direct))
				return direct;

			string[] files;
			try
			{
				files = Directory.GetFiles(packagesFolder, "*", SearchOption.AllDirectories);
			}
			catch
			{
				return null;
			}

			if (files == null || files.Length == 0)
				return null;

			// 2) Exact file name match
			var exact = files.FirstOrDefault(f =>
				string.Equals(Path.GetFileName(f), fileNameFromBgm, StringComparison.OrdinalIgnoreCase));
			if (!string.IsNullOrWhiteSpace(exact))
				return exact;

			// 3) Exact name-without-extension match
			var noExt = files.FirstOrDefault(f =>
				string.Equals(Path.GetFileNameWithoutExtension(f), fileNameFromBgm, StringComparison.OrdinalIgnoreCase));
			if (!string.IsNullOrWhiteSpace(noExt))
				return noExt;

			// 4) Starts-with fallback, useful when the bgm stores a display name but the file has an extension/suffix
			var startsWith = files.FirstOrDefault(f =>
				Path.GetFileName(f).StartsWith(fileNameFromBgm, StringComparison.OrdinalIgnoreCase));
			if (!string.IsNullOrWhiteSpace(startsWith))
				return startsWith;

			return null;
		}

		private async Task<T> GetJson<T>(string url, CancellationToken ct)
		{
			_log($"GET {url}");
			using (var req = UnityWebRequest.Get(url))
			{
				req.downloadHandler = new DownloadHandlerBuffer();
				var op = req.SendWebRequest();

				while (!op.isDone)
				{
					if (ct.IsCancellationRequested)
					{
						req.Abort();
						ct.ThrowIfCancellationRequested();
					}
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
					throw new Exception($"GET failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

				return JsonUtilitySafe.FromJson<T>(req.downloadHandler.text);
			}
		}

		private async Task<string> PostJsonRaw(string url, object body, CancellationToken ct)
		{
			_log($"POST {url}");
			var json = UnityEngine.JsonUtility.ToJson(body ?? new object());
			var bytes = Encoding.UTF8.GetBytes(json);

			using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");

				var op = req.SendWebRequest();
				while (!op.isDone)
				{
					if (ct.IsCancellationRequested)
					{
						req.Abort();
						ct.ThrowIfCancellationRequested();
					}
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
					throw new Exception($"POST failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

				return req.downloadHandler?.text ?? "";
			}
		}

		private static bool HasAnyPackages(PackagesBlock pkgs)
		{
			if (pkgs == null) return false;
			return pkgs.value != null && pkgs.value.Length > 0;
		}

		[Serializable]
		private sealed class ModuleBgm
		{
			public string id;
			public string name;
			public string type;
			public string controller;
			public string description;
			public string matchDescription;
			public string author;
			public string url;
			public ModuleBgmPackage[] packages;
			public string[] filesToRemove;
		}

		[Serializable]
		private sealed class ModuleBgmPackage
		{
			public string name;
			public string fileName;
			public string assetFolder;
		}
	}

	internal static class JsonUtilitySafe
	{
		[Serializable] private class Wrapper<T> { public T value; }

		public static T FromJson<T>(string json)
		{
			json = json?.Trim() ?? "";
			if (json.StartsWith("["))
			{
				var wrapped = "{\"value\":" + json + "}";
				return UnityEngine.JsonUtility.FromJson<Wrapper<T>>(wrapped).value;
			}
			return UnityEngine.JsonUtility.FromJson<T>(json);
		}
	}
}
#endif
