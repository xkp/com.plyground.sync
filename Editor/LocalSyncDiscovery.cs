#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Plysync.Editor
{
	public static class LocalSyncDiscovery
	{
		[Serializable]
		private sealed class VariationDescriptor
		{
			public string userVariationId;
			public string name;
			public string projectId;
			public string seed;
			public bool customImage;
			public string drawingId;
			public string configFileId;
			public string gameFileId;
		}

		public static SyncBuildInfo[] Discover(Action<string> log)
		{
			log ??= _ => { };

			var searchRoot = GetVariantSearchRoot();
			if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
			{
				log("Variant search root was not found two levels above the Unity project.");
				return Array.Empty<SyncBuildInfo>();
			}

			log($"Inspecting variant root: {searchRoot}");

			var candidates = new List<SyncBuildInfo>();
			var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var root in EnumerateCandidateRoots(searchRoot))
			{
				if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
					continue;

				if (!seenRoots.Add(root))
					continue;

				if (TryBuildInfo(root, log, out var info))
					candidates.Add(info);
			}

			log($"Local discovery found {candidates.Count} candidate project(s).");

			return candidates
				.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public static bool TryFindByRoot(string rootPath, out SyncBuildInfo info)
		{
			return TryFindByRoot(rootPath, null, out info);
		}

		public static bool TryFindByRoot(string rootPath, Action<string> log, out SyncBuildInfo info)
		{
			info = null;

			if (string.IsNullOrWhiteSpace(rootPath))
				return false;

			if (!Directory.Exists(rootPath))
				return false;

			return TryBuildInfo(rootPath, log, out info);
		}

		public static string GetInboxFolderAbsolutePath()
		{
			return Path.Combine(Application.dataPath, "plyground", "inbox");
		}

		public static string GetVariantSearchRoot()
		{
			var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			var parent = Directory.GetParent(projectRoot)?.FullName;
			return string.IsNullOrWhiteSpace(parent)
				? null
				: Directory.GetParent(parent)?.FullName;
		}

		private static IEnumerable<string> EnumerateCandidateRoots(string searchRoot)
		{
			yield return searchRoot;

			string[] children;
			try
			{
				children = Directory.GetDirectories(searchRoot);
			}
			catch
			{
				yield break;
			}

			foreach (var child in children)
				yield return child;
		}

		private static bool TryBuildInfo(string root, Action<string> log, out SyncBuildInfo info)
		{
			info = null;
			log ??= _ => { };

			var folderName = new DirectoryInfo(root).Name;

			// 1) Find a file called as the containing folder and read it as JSON.
			var variationFilePath = FindVariationDescriptorFile(root, folderName);
			if (string.IsNullOrWhiteSpace(variationFilePath))
			{
				log($"Skipping '{root}': could not find variation descriptor file named '{folderName}' or '{folderName}.json'.");
				return false;
			}

			if (!TryReadVariationDescriptor(variationFilePath, out var descriptor, log))
			{
				log($"Skipping '{root}': failed to parse variation descriptor '{variationFilePath}'.");
				return false;
			}

			// 2) Take the name and gameFileId from there, make sure it exists.
			if (descriptor == null)
			{
				log($"Skipping '{root}': descriptor is null.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(descriptor.name))
			{
				log($"Skipping '{root}': descriptor is missing 'name'.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(descriptor.gameFileId))
			{
				log($"Skipping '{root}': descriptor is missing 'gameFileId'.");
				return false;
			}

			var gameItemPath = FindGameFile(root, descriptor.gameFileId);
			if (string.IsNullOrWhiteSpace(gameItemPath))
			{
				log($"Skipping '{root}': could not find game file for gameFileId '{descriptor.gameFileId}'.");
				return false;
			}

			// 3) environment path is at ..\..\jobs\{variationName}\{seed}*.json and there must be a threedee.json file there.
			var environmentPath = FindEnvironmentPath(root, folderName, descriptor.seed, log);
			if (string.IsNullOrWhiteSpace(environmentPath))
			{
				log($"Skipping '{root}': could not resolve environment path for variation '{descriptor.name}' and seed '{descriptor.seed}'.");
				return false;
			}

			// 4) module path should be ..\..\biggame\modules
			var modulePath = FindModulePath(root);
			if (string.IsNullOrWhiteSpace(modulePath) || !Directory.Exists(modulePath))
			{
				log($"Skipping '{root}': module path was not found.");
				return false;
			}

			// assetPath is not explicitly described in your rules, but SyncBuildInfo has the field.
			// Using the parallel location: ..\..\biggame\assets
			var assetPath = FindAssetPath(root);

			// 5) build file path is on the same folder under the name build.json
			var buildFilePath = Path.Combine(root, "build.json");
			if (!File.Exists(buildFilePath))
			{
				log($"Skipping '{root}': build.json was not found.");
				return false;
			}

			info = new SyncBuildInfo
			{
				name = descriptor.name,
				variationId = descriptor.userVariationId,
				path = root,
				environmentPath = environmentPath,
				gameItemPath = gameItemPath,
				buildFilePath = buildFilePath,
				modulePath = modulePath,
				assetPath = assetPath
			};

			return true;
		}

		private static string FindVariationDescriptorFile(string root, string folderName)
		{
			var exactNoExt = Path.Combine(root, folderName);
			if (File.Exists(exactNoExt))
				return exactNoExt;

			var exactJson = Path.Combine(root, folderName + ".json");
			if (File.Exists(exactJson))
				return exactJson;

			return null;
		}

		private static bool TryReadVariationDescriptor(string path, out VariationDescriptor descriptor, Action<string> log)
		{
			descriptor = null;

			try
			{
				var json = File.ReadAllText(path);
				if (string.IsNullOrWhiteSpace(json))
					return false;

				descriptor = JsonUtility.FromJson<VariationDescriptor>(json);
				return descriptor != null;
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed reading variation descriptor '{path}': {ex.Message}");
				return false;
			}
		}

		private static string FindGameFile(string root, string gameFileId)
		{
			if (string.IsNullOrWhiteSpace(gameFileId))
				return null;

			// Prefer exact common cases in the root first.
			var candidates = new[]
			{
				Path.Combine(root, gameFileId),
				Path.Combine(root, gameFileId + ".json")
			};

			foreach (var candidate in candidates)
			{
				if (File.Exists(candidate))
					return candidate;
			}

			// Fall back to an immediate search under root only.
			try
			{
				var files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly);
				foreach (var file in files)
				{
					var nameNoExt = Path.GetFileNameWithoutExtension(file);
					var fileName = Path.GetFileName(file);

					if (string.Equals(nameNoExt, gameFileId, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(fileName, gameFileId, StringComparison.OrdinalIgnoreCase))
						return file;
				}
			}
			catch
			{
				// ignore
			}

			return null;
		}

		private static string FindEnvironmentPath(string root, string variationName, string seed, Action<string> log)
		{
			if (string.IsNullOrWhiteSpace(variationName) || string.IsNullOrWhiteSpace(seed))
				return null;

			var jobsVariationDir = Path.GetFullPath(Path.Combine(root, "..", "..", "jobs", variationName));
			if (!Directory.Exists(jobsVariationDir))
			{
				log?.Invoke($"Jobs variation folder not found: {jobsVariationDir}");
				return null;
			}

			string[] seedJsonFiles;
			try
			{
				seedJsonFiles = Directory.GetFiles(jobsVariationDir, seed + "*.json", SearchOption.TopDirectoryOnly);
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed searching seed json files in '{jobsVariationDir}': {ex.Message}");
				return null;
			}

			if (seedJsonFiles == null || seedJsonFiles.Length == 0)
			{
				log?.Invoke($"No seed json files found in '{jobsVariationDir}' for pattern '{seed}*.json'.");
				return null;
			}

			foreach (var seedJsonFile in seedJsonFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
			{
				var seedFolder = Path.Combine(jobsVariationDir, Path.GetFileNameWithoutExtension(seedJsonFile));
				var threedeeJson = Path.Combine(seedFolder, "threedee_scene.json");

				if (Directory.Exists(seedFolder) && File.Exists(threedeeJson))
					return seedFolder;
			}

			log?.Invoke($"Seed json files were found, but no matching folder with threedee.json was found under '{jobsVariationDir}'.");
			return null;
		}

		private static string FindModulePath(string root)
		{
			try
			{
				var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				var path = Path.Combine(userFolder, "threedee", "cache", "modules");
				return Directory.Exists(path) ? path : null;
			}
			catch
			{
				return null;
			}
		}

		private static string FindAssetPath(string root)
		{
			try
			{
				var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				var path = Path.Combine(userFolder, "threedee", "userAssets");
				return Directory.Exists(path) ? path : null;
			}
			catch
			{
				return null;
			}
		}
	}
}
#endif
