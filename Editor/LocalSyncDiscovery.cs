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
			public string moduleId;
			public string templateId;
			public PlygroundInstalledModule[] installedModules;
		}

		[Serializable]
		private sealed class PlygroundInstalledModule
		{
			public string moduleId;
			public string moduleName;
			public string modulePath;
			public string templatePath;
			public string packagesPath;
			public string assetsPath;
			public string plygroundPath;
		}

		[Serializable]
		private sealed class PlygroundBob
		{
			public string status;
			public string outputFolder;
			public string fbxFolder;
			public string sceneFile;
		}

		public static SyncBuildInfo[] Discover(Action<string> log)
		{
			log ??= _ => { };

			if (TryDiscoverCurrentProject(log, out var info))
			{
				log("Local discovery resolved from Assets/.plyground.");
				return new[] { info };
			}

			return Array.Empty<SyncBuildInfo>();
		}

		public static bool TryDiscoverCurrentProject(out SyncBuildInfo info)
		{
			return TryDiscoverCurrentProject(null, out info);
		}

		public static bool TryDiscoverCurrentProject(Action<string> log, out SyncBuildInfo info)
		{
			return TryDiscoverFromProjectFile(log, out info);
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

			if (!TryDiscoverCurrentProject(log, out info) || info == null)
				return false;

			return string.Equals(
				Path.GetFullPath(info.path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
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

		private static bool TryBuildInfoFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, Action<string> log, out SyncBuildInfo info)
		{
			info = null;
			log ??= _ => { };

			var projectFileDirectory = Path.GetDirectoryName(projectFilePath);
			var rawVariationFolder = projectFile?.variation?.folder;
			var root = ResolveProjectFilePath(projectFileDirectory, rawVariationFolder);
			if (string.IsNullOrWhiteSpace(rawVariationFolder))
			{
				log("Project .plyground is missing 'variation.folder'.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(root))
			{
				log($"Project .plyground variation.folder could not be resolved: '{rawVariationFolder}'.");
				return false;
			}

			if (!Directory.Exists(root))
			{
				log($"Project .plyground variation.folder does not exist: '{root}'.");
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

			if (!TryResolveVariationFiles(
				root,
				variationId,
				log,
				out var descriptor,
				out var descriptorFilePath,
				out var gameItemPath,
				out var buildFilePath))
			{
				return false;
			}

			if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(descriptor?.name))
				name = descriptor.name.Trim();

			if (string.IsNullOrWhiteSpace(name))
			{
				log("Project .plyground did not provide a variation name.");
				return false;
			}

			log($"Project .plyground resolved variation descriptor '{descriptorFilePath}'.");
			log($"Project .plyground resolved game payload '{gameItemPath}'.");

			var environmentPath = ResolveEnvironmentPathFromProjectFile(projectFilePath, projectFile, descriptor?.seed, log);
			if (string.IsNullOrWhiteSpace(environmentPath))
			{
				log("Project .plyground did not provide a usable bob scene path.");
				return false;
			}

			var modulePath = ResolveModulePathFromProjectFile(projectFilePath, projectFile, log);
			if (string.IsNullOrWhiteSpace(modulePath) || !Directory.Exists(modulePath))
			{
				log($"Skipping '{root}': module path was not found.");
				return false;
			}

			var assetPath = ResolveAssetPathFromProjectFile(projectFilePath, projectFile, log);

			info = new SyncBuildInfo
			{
				name = name,
				variationId = variationId,
				path = root,
				environmentPath = environmentPath,
				gameItemPath = gameItemPath,
				buildFilePath = buildFilePath,
				modulePath = modulePath,
				assetPath = assetPath
			};

			return true;
		}

		private static bool TryResolveVariationFiles(
			string root,
			string variationId,
			Action<string> log,
			out VariationDescriptor descriptor,
			out string descriptorFilePath,
			out string gameItemPath,
			out string buildFilePath)
		{
			descriptor = null;
			descriptorFilePath = null;
			gameItemPath = null;
			buildFilePath = null;
			log ??= _ => { };

			descriptorFilePath = FindVariationDescriptorFile(root, variationId);
			if (string.IsNullOrWhiteSpace(descriptorFilePath))
			{
				log($"Skipping '{root}': could not find variation descriptor file named '{variationId}' or '{variationId}.json'.");
				return false;
			}

			log($"Using variation descriptor '{descriptorFilePath}'.");

			if (!TryReadVariationDescriptor(descriptorFilePath, out descriptor, log))
			{
				log($"Skipping '{root}': failed to parse variation descriptor '{descriptorFilePath}'.");
				return false;
			}

			if (descriptor == null)
			{
				log($"Skipping '{root}': descriptor is null.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(descriptor.gameFileId))
			{
				log($"Skipping '{root}': descriptor '{descriptorFilePath}' is missing 'gameFileId'.");
				return false;
			}

			gameItemPath = FindGameFile(root, descriptor.gameFileId, log);
			if (string.IsNullOrWhiteSpace(gameItemPath))
			{
				log($"Skipping '{root}': descriptor '{descriptorFilePath}' requested gameFileId '{descriptor.gameFileId}', but no matching payload file was found.");
				return false;
			}

			buildFilePath = Path.Combine(root, "build.json");
			if (!File.Exists(buildFilePath))
			{
				log($"Skipping '{root}': build.json was not found.");
				return false;
			}

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

		private static string ResolveEnvironmentPathFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, string seed, Action<string> log)
		{
			log ??= _ => { };

			var projectFileDirectory = Path.GetDirectoryName(projectFilePath);
			var sceneFilePath = ResolveProjectFilePath(projectFileDirectory, projectFile?.bob?.sceneFile);
			if (!string.IsNullOrWhiteSpace(sceneFilePath) && File.Exists(sceneFilePath))
				return Path.GetDirectoryName(sceneFilePath);

			var bobOutputFolder = ResolveProjectFilePath(projectFileDirectory, projectFile?.bob?.outputFolder);
			var outputFolderScenePath = TryResolveSceneDirectoryNearFolderHint(
				bobOutputFolder,
				$"Project .plyground outputFolder '{bobOutputFolder}'",
				log);
			if (!string.IsNullOrWhiteSpace(outputFolderScenePath))
				return outputFolderScenePath;

			var bobFbxFolder = ResolveProjectFilePath(projectFileDirectory, projectFile?.bob?.fbxFolder);
			var fbxFolderScenePath = TryResolveSceneDirectoryNearFolderHint(
				bobFbxFolder,
				$"Project .plyground fbxFolder '{bobFbxFolder}'",
				log);
			if (!string.IsNullOrWhiteSpace(fbxFolderScenePath))
				return fbxFolderScenePath;

			var variationRoot = ResolveProjectFilePath(projectFileDirectory, projectFile?.variation?.folder);
			if (!string.IsNullOrWhiteSpace(variationRoot))
			{
				log("Project .plyground did not resolve directly to a bob scene. Falling back to variation .bob discovery.");
				return FindEnvironmentPathInBob(variationRoot, seed, log);
			}

			return null;
		}

		private static string ResolveModulePathFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, Action<string> log)
		{
			log ??= _ => { };

			if (TryResolveInstalledModuleSelection(projectFilePath, projectFile, log, out var selectedModule, out var installedModules))
			{
				var selectedRoot = GetParentDirectory(selectedModule.modulePath);
				if (!string.IsNullOrWhiteSpace(selectedRoot) && Directory.Exists(selectedRoot))
				{
					log($"Project .plyground resolved module cache root '{selectedRoot}' from unity.installedModules.");
					return selectedRoot;
				}
			}

			var commonRoot = TryResolveCommonModuleRoot(projectFilePath, installedModules, log);
			if (!string.IsNullOrWhiteSpace(commonRoot))
			{
				log($"Project .plyground resolved module cache root '{commonRoot}' from unity.installedModules.");
				return commonRoot;
			}

			var fallback = FindModulePath();
			if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
			{
				log($"Project .plyground fell back to legacy module cache root '{fallback}'.");
				return fallback;
			}

			return fallback;
		}

		private static string ResolveAssetPathFromProjectFile(string projectFilePath, PlygroundProjectFile projectFile, Action<string> log)
		{
			log ??= _ => { };

			if (TryResolveInstalledModuleSelection(projectFilePath, projectFile, log, out var selectedModule, out var installedModules))
			{
				var selectedAssetsPath = ResolveProjectFilePath(Path.GetDirectoryName(projectFilePath), selectedModule.assetsPath);
				if (!string.IsNullOrWhiteSpace(selectedAssetsPath) && Directory.Exists(selectedAssetsPath))
				{
					log($"Project .plyground resolved asset cache path '{selectedAssetsPath}' from unity.installedModules.");
					return selectedAssetsPath;
				}
			}

			foreach (var installedModule in installedModules ?? Array.Empty<PlygroundInstalledModule>())
			{
				var candidate = ResolveProjectFilePath(Path.GetDirectoryName(projectFilePath), installedModule?.assetsPath);
				if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
				{
					log($"Project .plyground resolved asset cache path '{candidate}' from unity.installedModules.");
					return candidate;
				}
			}

			var fallback = FindAssetPath();
			if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
			{
				log($"Project .plyground fell back to legacy asset cache path '{fallback}'.");
				return fallback;
			}

			return fallback;
		}

		private static bool TryResolveInstalledModuleSelection(
			string projectFilePath,
			PlygroundProjectFile projectFile,
			Action<string> log,
			out PlygroundInstalledModule selectedModule,
			out PlygroundInstalledModule[] installedModules)
		{
			selectedModule = null;
			installedModules = projectFile?.unity?.installedModules ?? Array.Empty<PlygroundInstalledModule>();

			if (installedModules.Length == 0)
				return false;

			var selectedModuleId = (projectFile?.unity?.moduleId ?? "").Trim();
			if (!string.IsNullOrWhiteSpace(selectedModuleId))
			{
				selectedModule = installedModules.FirstOrDefault(module =>
					string.Equals((module?.moduleId ?? "").Trim(), selectedModuleId, StringComparison.OrdinalIgnoreCase));
				if (selectedModule != null)
					return true;

				log?.Invoke($"Project .plyground unity.moduleId '{selectedModuleId}' was not found in unity.installedModules.");
			}

			selectedModule = installedModules.FirstOrDefault(module =>
			{
				var resolved = ResolveProjectFilePath(Path.GetDirectoryName(projectFilePath), module?.modulePath);
				return !string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved);
			});

			return selectedModule != null;
		}

		private static string TryResolveCommonModuleRoot(string projectFilePath, PlygroundInstalledModule[] installedModules, Action<string> log)
		{
			var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var installedModule in installedModules ?? Array.Empty<PlygroundInstalledModule>())
			{
				var modulePath = ResolveProjectFilePath(Path.GetDirectoryName(projectFilePath), installedModule?.modulePath);
				if (string.IsNullOrWhiteSpace(modulePath) || !Directory.Exists(modulePath))
					continue;

				var parent = GetParentDirectory(modulePath);
				if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
					roots.Add(parent);
			}

			if (roots.Count == 1)
				return roots.First();

			if (roots.Count > 1)
				log?.Invoke("Project .plyground unity.installedModules resolved multiple module cache roots. Falling back to the selected module root.");

			return null;
		}

		private static string GetParentDirectory(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return null;

			try
			{
				return Directory.GetParent(path)?.FullName;
			}
			catch
			{
				return null;
			}
		}

		private static string TryResolveSceneDirectoryNearFolderHint(string folderPath, string label, Action<string> log)
		{
			if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
				return null;

			try
			{
				if (File.Exists(Path.Combine(folderPath, "threedee_scene.json")))
					return folderPath;

				var childMatches = Directory
					.GetDirectories(folderPath, "*", SearchOption.TopDirectoryOnly)
					.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
					.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (childMatches.Length == 1)
					return childMatches[0];

				if (childMatches.Length > 1)
				{
					log?.Invoke($"{label} contains multiple scene folders.");
					return null;
				}

				var parentFolder = Directory.GetParent(folderPath)?.FullName;
				if (!string.IsNullOrWhiteSpace(parentFolder) && Directory.Exists(parentFolder))
				{
					if (File.Exists(Path.Combine(parentFolder, "threedee_scene.json")))
						return parentFolder;

					var siblingMatches = Directory
						.GetDirectories(parentFolder, "*", SearchOption.TopDirectoryOnly)
						.Where(dir => !string.Equals(dir, folderPath, StringComparison.OrdinalIgnoreCase))
						.Where(dir => File.Exists(Path.Combine(dir, "threedee_scene.json")))
						.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
						.ToArray();

					if (siblingMatches.Length == 1)
						return siblingMatches[0];

					if (siblingMatches.Length > 1)
					{
						log?.Invoke($"{label} parent folder contains multiple sibling scene folders.");
						return null;
					}
				}
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed scanning {label}: {ex.Message}");
				return null;
			}

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

		private static string FindGameFile(string root, string gameFileId, Action<string> log = null)
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

			log?.Invoke($"Game payload lookup in '{root}' first tried '{candidates[0]}' and '{candidates[1]}'.");

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

		private static string FindEnvironmentPathInBob(string root, string seed, Action<string> log)
		{
			if (string.IsNullOrWhiteSpace(root))
				return null;

			var bobDir = Path.Combine(root, ".bob");
			if (!Directory.Exists(bobDir))
			{
				log?.Invoke($"Variation .bob folder not found: {bobDir}");
				bobDir = root;
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

		private static string FindModulePath()
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

		private static string FindAssetPath()
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
