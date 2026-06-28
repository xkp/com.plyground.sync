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

		[Serializable]
		private sealed class PlygroundProjectFile
		{
			public string schema;
			public string format;
			public int version;
			public string generatedAt;
			public PlygroundVariation variation;
			public PlygroundUnity unity;
			public PlygroundBob bob;
		}

		[Serializable]
		private sealed class PlygroundVariation
		{
			public string id;
			public string name;
			public string folder;
		}

		[Serializable]
		private sealed class PlygroundUnity
		{
			public string projectFolder;
			public string assetsFolder;
		}

		[Serializable]
		private sealed class PlygroundBob
		{
			public string status;
			public string outputFolder;
			public string fbxFolder;
			public string sceneFile;
		}

		[Serializable]
		private sealed class BuildDescriptor
		{
			public string projectName;
			public string selectedGame;
		}

		public static SyncBuildInfo[] Discover(Action<string> log)
		{
			log ??= _ => { };

			var projectFilePath = Path.Combine(Application.dataPath, ".plyground");
			if (File.Exists(projectFilePath))
			{
				if (TryDiscoverFromProjectFile(log, out var projectInfo))
				{
					log("Local discovery resolved from Assets/.plyground.");
					return new[] { projectInfo };
				}

				log("Assets/.plyground is present but did not resolve to a complete payload.");
				return Array.Empty<SyncBuildInfo>();
			}

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

		public static string GetVariationIdFromRoot(string rootPath)
		{
			if (string.IsNullOrWhiteSpace(rootPath))
				return null;

			try
			{
				var normalizedRoot = Path.GetFullPath(rootPath)
					.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				var folderName = Path.GetFileName(normalizedRoot);
				return string.IsNullOrWhiteSpace(folderName) ? null : folderName;
			}
			catch
			{
				return null;
			}
		}

		public static string GetVariantSearchRoot()
		{
			var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			var parent = Directory.GetParent(projectRoot)?.FullName;
			return string.IsNullOrWhiteSpace(parent)
				? null
				: Directory.GetParent(parent)?.FullName;
		}

		private static bool TryDiscoverFromProjectFile(Action<string> log, out SyncBuildInfo info)
		{
			info = null;
			log ??= _ => { };

			var projectFilePath = Path.Combine(Application.dataPath, ".plyground");
			if (!File.Exists(projectFilePath))
			{
				log($"Project .plyground file not found at '{projectFilePath}'.");
				return false;
			}

			if (!TryReadProjectFile(projectFilePath, out var projectFile, log) || projectFile == null)
			{
				log($"Failed to parse project .plyground file '{projectFilePath}'.");
				return false;
			}

			if (!TryBuildInfoFromProjectFile(projectFilePath, projectFile, log, out info))
			{
				log($"Project .plyground file '{projectFilePath}' did not contain a complete payload.");
				return false;
			}

			return true;
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

		private static bool TryBuildInfoFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, Action<string> log, out SyncBuildInfo info)
		{
			info = null;
			log ??= _ => { };

			var projectFileDirectory = Path.GetDirectoryName(projectFilePath);
			var root = ResolveProjectFilePath(projectFileDirectory, projectFile?.variation?.folder);
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
			{
				log("Project .plyground is missing a valid variation.folder path.");
				return false;
			}

			var variationId = !string.IsNullOrWhiteSpace(projectFile?.variation?.id)
				? projectFile.variation.id.Trim()
				: GetVariationIdFromRoot(root);
			var name = !string.IsNullOrWhiteSpace(projectFile?.variation?.name)
				? projectFile.variation.name.Trim()
				: variationId;

			if (string.IsNullOrWhiteSpace(variationId))
			{
				log("Project .plyground did not provide a variation id and one could not be derived from the variation folder.");
				return false;
			}

			var buildFilePath = Path.Combine(root, "build.json");
			if (!File.Exists(buildFilePath))
			{
				log($"Project variation root '{root}' is missing build.json.");
				return false;
			}

			if (!TryReadBuildDescriptor(buildFilePath, out var buildDescriptor, log) || buildDescriptor == null)
			{
				log($"Project variation build.json '{buildFilePath}' could not be parsed.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(buildDescriptor.projectName))
				name = buildDescriptor.projectName.Trim();

			if (string.IsNullOrWhiteSpace(name))
			{
				log("Project .plyground did not provide a variation name.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(buildDescriptor.selectedGame))
			{
				log($"Project variation build.json '{buildFilePath}' is missing selectedGame.");
				return false;
			}

			var gameItemPath = FindGameFile(root, buildDescriptor.selectedGame);
			if (string.IsNullOrWhiteSpace(gameItemPath))
			{
				log($"Project variation root '{root}' is missing the selectedGame payload '{buildDescriptor.selectedGame}'.");
				return false;
			}

			var environmentPath = ResolveEnvironmentPathFromProjectFile(projectFilePath, projectFile, log);
			if (string.IsNullOrWhiteSpace(environmentPath))
			{
				log("Project .plyground did not provide a usable bob scene path.");
				return false;
			}

			var modulePath = FindModulePath(root);
			if (string.IsNullOrWhiteSpace(modulePath) || !Directory.Exists(modulePath))
			{
				log($"Skipping '{root}': module path was not found.");
				return false;
			}

			info = new SyncBuildInfo
			{
				name = name,
				variationId = variationId,
				path = root,
				environmentPath = environmentPath,
				gameItemPath = gameItemPath,
				buildFilePath = buildFilePath,
				modulePath = modulePath,
				assetPath = FindAssetPath(root)
			};

			return true;
		}

		private static bool TryBuildInfo(string root, Action<string> log, out SyncBuildInfo info)
		{
			info = null;
			log ??= _ => { };

			var folderName = GetVariationIdFromRoot(root);
			if (string.IsNullOrWhiteSpace(folderName))
			{
				log($"Skipping '{root}': could not determine the variation folder name.");
				return false;
			}

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

			var environmentPath = FindEnvironmentPath(root, folderName, descriptor.seed, log);
			if (string.IsNullOrWhiteSpace(environmentPath))
			{
				log($"Skipping '{root}': could not resolve environment path for variation '{descriptor.name}' and seed '{descriptor.seed}'.");
				return false;
			}

			var modulePath = FindModulePath(root);
			if (string.IsNullOrWhiteSpace(modulePath) || !Directory.Exists(modulePath))
			{
				log($"Skipping '{root}': module path was not found.");
				return false;
			}

			var assetPath = FindAssetPath(root);
			var buildFilePath = Path.Combine(root, "build.json");
			if (!File.Exists(buildFilePath))
			{
				log($"Skipping '{root}': build.json was not found.");
				return false;
			}

			info = new SyncBuildInfo
			{
				name = descriptor.name,
				variationId = folderName,
				path = root,
				environmentPath = environmentPath,
				gameItemPath = gameItemPath,
				buildFilePath = buildFilePath,
				modulePath = modulePath,
				assetPath = assetPath
			};

			return true;
		}

		private static bool TryReadProjectFile(string path, out PlygroundProjectFile projectFile, Action<string> log)
		{
			projectFile = null;

			try
			{
				var json = File.ReadAllText(path);
				if (string.IsNullOrWhiteSpace(json))
					return false;

				projectFile = JsonUtility.FromJson<PlygroundProjectFile>(json);
				return projectFile != null;
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed reading project .plyground '{path}': {ex.Message}");
				return false;
			}
		}

		private static bool TryReadBuildDescriptor(string path, out BuildDescriptor buildDescriptor, Action<string> log)
		{
			buildDescriptor = null;

			try
			{
				var json = File.ReadAllText(path);
				if (string.IsNullOrWhiteSpace(json))
					return false;

				buildDescriptor = JsonUtility.FromJson<BuildDescriptor>(json);
				return buildDescriptor != null;
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed reading build file '{path}': {ex.Message}");
				return false;
			}
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

		private static string ResolveProjectFilePath(string projectFileDirectory, string relativeOrAbsolutePath)
		{
			if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
				return null;

			try
			{
				return Path.IsPathRooted(relativeOrAbsolutePath)
					? Path.GetFullPath(relativeOrAbsolutePath)
					: Path.GetFullPath(Path.Combine(projectFileDirectory, relativeOrAbsolutePath));
			}
			catch
			{
				return null;
			}
		}

		private static string ResolveEnvironmentPathFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, Action<string> log)
		{
			log ??= _ => { };

			var projectFileDirectory = Path.GetDirectoryName(projectFilePath);
			var sceneFilePath = ResolveProjectFilePath(projectFileDirectory, projectFile?.bob?.sceneFile);
			if (!string.IsNullOrWhiteSpace(sceneFilePath) && File.Exists(sceneFilePath))
				return Path.GetDirectoryName(sceneFilePath);

			var bobOutputFolder = ResolveProjectFilePath(projectFileDirectory, projectFile?.bob?.outputFolder);
			if (!string.IsNullOrWhiteSpace(bobOutputFolder) && Directory.Exists(bobOutputFolder))
			{
				var directScenePath = Path.Combine(bobOutputFolder, "threedee_scene.json");
				if (File.Exists(directScenePath))
					return bobOutputFolder;

				try
				{
					var matchingFolders = Directory
						.GetDirectories(bobOutputFolder, "*", SearchOption.TopDirectoryOnly)
						.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
						.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
						.ToArray();

					if (matchingFolders.Length == 1)
						return matchingFolders[0];

					if (matchingFolders.Length > 1)
					{
						log($"Project .plyground outputFolder '{bobOutputFolder}' contains multiple scene folders.");
						return null;
					}
				}
				catch (Exception ex)
				{
					log($"Failed scanning project .plyground outputFolder '{bobOutputFolder}': {ex.Message}");
					return null;
				}
			}

			var variationRoot = ResolveProjectFilePath(projectFileDirectory, projectFile?.variation?.folder);
			if (!string.IsNullOrWhiteSpace(variationRoot))
				return FindEnvironmentPathInBob(variationRoot, null, log);

			return null;
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

		private static string FindGameFile(string root, string gameFileId)
		{
			if (string.IsNullOrWhiteSpace(gameFileId))
				return null;

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
			}

			return null;
		}

		private static string FindEnvironmentPath(string root, string variationName, string seed, Action<string> log)
		{
			if (string.IsNullOrWhiteSpace(variationName))
				return null;

			var jobsVariationDir = Path.GetFullPath(Path.Combine(root, "..", "..", "jobs", variationName));
			if (!Directory.Exists(jobsVariationDir))
			{
				log?.Invoke($"Jobs variation folder not found: {jobsVariationDir}");
				return FindEnvironmentPathInBob(root, seed, log);
			}

			if (!string.IsNullOrWhiteSpace(seed))
			{
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

				if (seedJsonFiles != null && seedJsonFiles.Length > 0)
				{
					foreach (var seedJsonFile in seedJsonFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
					{
						var seedFolder = Path.Combine(jobsVariationDir, Path.GetFileNameWithoutExtension(seedJsonFile));
						var threedeeJson = Path.Combine(seedFolder, "threedee_scene.json");

						if (Directory.Exists(seedFolder) && File.Exists(threedeeJson))
							return seedFolder;
					}

					log?.Invoke($"Seed json files were found, but no matching folder with threedee_scene.json was found under '{jobsVariationDir}'.");
				}
				else
				{
					log?.Invoke($"No seed json files found in '{jobsVariationDir}' for pattern '{seed}*.json'. Falling back to folder scan.");
				}
			}

			try
			{
				var matchingFolders = Directory
					.GetDirectories(jobsVariationDir, "*", SearchOption.TopDirectoryOnly)
					.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
					.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (matchingFolders.Length == 1)
				{
					log?.Invoke($"Using fallback environment folder: {matchingFolders[0]}");
					return matchingFolders[0];
				}

				if (matchingFolders.Length > 1)
				{
					log?.Invoke($"Environment fallback found multiple folders with threedee_scene.json under '{jobsVariationDir}'.");
					return null;
				}
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed scanning fallback environment folders in '{jobsVariationDir}': {ex.Message}");
				return null;
			}

			log?.Invoke($"No environment folder containing threedee_scene.json was found under '{jobsVariationDir}'. Trying variation .bob.");
			return FindEnvironmentPathInBob(root, seed, log);
		}

		private static string FindEnvironmentPathInBob(string root, string seed, Action<string> log)
		{
			if (string.IsNullOrWhiteSpace(root))
				return null;

			var bobDir = Path.Combine(root, ".bob");
			if (!Directory.Exists(bobDir))
			{
				log?.Invoke($"Variation .bob folder not found: {bobDir}");
				return null;
			}

			var directSceneFile = Path.Combine(bobDir, "threedee_scene.json");
			if (File.Exists(directSceneFile))
				return bobDir;

			if (!string.IsNullOrWhiteSpace(seed))
			{
				try
				{
					var seededFolders = Directory
						.GetDirectories(bobDir, seed + "*", SearchOption.TopDirectoryOnly)
						.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
						.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
						.ToArray();

					if (seededFolders.Length == 1)
						return seededFolders[0];

					if (seededFolders.Length > 1)
					{
						log?.Invoke($"Variation .bob seed fallback found multiple folders with threedee_scene.json under '{bobDir}'.");
						return null;
					}
				}
				catch (Exception ex)
				{
					log?.Invoke($"Failed scanning seeded environment folders in '{bobDir}': {ex.Message}");
					return null;
				}
			}

			try
			{
				var matchingFolders = Directory
					.GetDirectories(bobDir, "*", SearchOption.TopDirectoryOnly)
					.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
					.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (matchingFolders.Length == 1)
				{
					log?.Invoke($"Using variation .bob fallback environment folder: {matchingFolders[0]}");
					return matchingFolders[0];
				}

				if (matchingFolders.Length > 1)
				{
					log?.Invoke($"Variation .bob fallback found multiple folders with threedee_scene.json under '{bobDir}'.");
					return null;
				}
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed scanning variation .bob fallback environment folders in '{bobDir}': {ex.Message}");
				return null;
			}

			log?.Invoke($"No environment artifact containing threedee_scene.json was found under '{bobDir}'.");
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
