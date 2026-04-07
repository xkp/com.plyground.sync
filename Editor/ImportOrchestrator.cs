#if UNITY_EDITOR
using Plysync.Editor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

	namespace Plyground.Editor
	{
		public enum ImportRunResult
		{
			Completed,
			DeferredForReload
		}

		public sealed class ImportOrchestrator
		{
		private readonly PlysyncClient _local;
		private readonly CacheStore _cache;
		private readonly Action<string> _log;
		private readonly Action<string, float> _progress;

		// You can later expose this in the UI if you want to tweak post-processing.
		private readonly IList<PostProcessNode> _defaultPostProcess = new List<PostProcessNode>();

		public ImportOrchestrator(PlysyncClient local, CacheStore cache, Action<string> log, Action<string, float> progress)
		{
			_local = local ?? new PlysyncClient(log);
			_cache = cache;
			_log = log ?? (_ => { });
			_progress = progress ?? ((_, __) => { });
		}

		public async Task<ImportRunResult> Run(SyncBuildInfo info, CancellationToken ct)
		{
			if (info == null) throw new Exception("SyncBuildInfo is null");
			if (string.IsNullOrWhiteSpace(info.path)) throw new Exception("info.path is required");

			var gameId = info.path; // stable ID for marker + cache

			// OPTIONAL: parse build.json for revision + packages (if present)
			string revision = "unknown";
			PackagesBlock packages = null;
			BuildJson buildJson = null;

			_progress("Reading build metadata...", 0.05f);

			if (!string.IsNullOrWhiteSpace(info.buildFilePath))
			{
				if (PathJsonLoader.TryLoadJsonFile<BuildJson>(info.buildFilePath, out buildJson) && buildJson != null)
				{
					if (!string.IsNullOrWhiteSpace(buildJson.revision))
						revision = buildJson.revision;

					packages = buildJson.packages;
				}
			}

			var moduleIds = ExtractModuleIds(buildJson);
			if (_local != null && moduleIds.Length > 0)
			{
				_log($"Resolved {moduleIds.Length} module id(s) from build.json.");
				_progress("Resolving packages from modules...", 0.10f);

				var resolvedPackages = _local.ResolvePackagesForModules(moduleIds, ct);
				packages = MergePackages(packages, resolvedPackages);
			}
			else if (moduleIds.Length > 0)
			{
				_log("Module ids found in build.json, but local package resolution is disabled in offline mode.");
			}
			else
			{
				_log("No module ids found in build.json for package resolution.");
			}

			// Packages must be installed before any import work so downstream importers
			// can rely on the required types and assets already being available.
			if (packages != null)
			{
				_cache.SaveSyncInfo(info);
				ImportSessionState.SavePendingImportPath(info.path);
				_log($"Prepared pending import resume state for: {info.path}");
				_progress("Installing packages...", 0.15f);
				var packagesChanged = await PackageInstaller.Install(packages, _log, ct);
				_log(packagesChanged
					? "Package install completed with project changes. Continuing import."
					: "Package install completed without project changes.");

				if (packagesChanged)
				{
					_log("Package changes detected. Deferring the rest of the import until Unity reloads assemblies.");
					return ImportRunResult.DeferredForReload;
				}
			}
			else
			{
				_log("No packages block (or build.json missing). Skipping package install.");
			}

			// Revision-based cache short-circuit (only if we actually have a revision)
			var cached = _cache.Read(gameId);
			if (cached != null && revision != "unknown" && cached.lastImportedRevision == revision)
			{
				_log($"No changes. revision={revision}");
				// still ensure marker has the latest paths
				//UpsertSceneMarker(gameId, revision, info);
				return ImportRunResult.Completed;
			}

			_progress("Opening MainScene...", 0.30f);
			OpenMainScene(_log);

			// Environment import (ThreedeeLoader)
			_progress("Importing environment (ThreedeeLoader)...", 0.55f);
			string outputFolder = Application.dataPath;
			_log("Starting environment import...");
			EnvironmentImporter.Import(
				gameId,
				revision,
				outputFolder,
				info,
				_defaultPostProcess,
				_log
			);
			_log("Environment import finished.");

			// Game items import (BigGameLoader)
			_progress("Importing game items (BigGameLoader)...", 0.72f);
			_log("Starting game items import via PlygroundLoader.Load...");

			var preprocess = new List<PostProcessNode>(); // keep empty for now, wire UI later if needed
			await PlygroundLoader.Load(
				info.gameItemPath,
				info.buildFilePath,
				info.modulePath,
				info.assetPath,
				preprocess
			);
			_log("Game items import finished.");

			// Save
			_progress("Saving assets...", 0.90f);
			AssetDatabase.SaveAssets();
			EditorSceneManager.SaveOpenScenes();

			// Cache + persist sync info
			_progress("Writing cache...", 0.94f);

			_cache.SaveSyncInfo(info);

			_cache.Write(new PlysyncCache
			{
				gameId = gameId,
				lastImportedRevision = revision,
				lastImportedAtUtc = DateTime.UtcNow.ToString("o")
			});

			// Ensure marker always has paths + timestamps
			//UpsertSceneMarker(gameId, revision, info);

			_progress("Done.", 1f);
			ImportSessionState.ClearPendingImportPath();
			return ImportRunResult.Completed;
		}

		public async Task RunSync(SyncBuildInfo info, CancellationToken ct)
		{
			if (info == null) throw new Exception("SyncBuildInfo is null");
			if (string.IsNullOrWhiteSpace(info.path)) throw new Exception("info.path is required");
			if (string.IsNullOrWhiteSpace(info.gameItemPath)) throw new Exception("info.gameItemPath is required");
			if (string.IsNullOrWhiteSpace(info.buildFilePath)) throw new Exception("info.buildFilePath is required");
			if (string.IsNullOrWhiteSpace(info.modulePath)) throw new Exception("info.modulePath is required");

			var gameId = info.path;
			var revision = ResolveRevision(info);
			var cached = _cache.Read(gameId);

			if (cached != null && revision != "unknown" && cached.lastImportedRevision == revision)
			{
				_log($"No sync changes detected. revision={revision}");
				EnvironmentImporter.UpdateMarker(gameId, revision, info, _log);
				_cache.SaveSyncInfo(info);
				WriteCache(gameId, revision);
				AssetDatabase.SaveAssets();
				EditorSceneManager.SaveOpenScenes();
				return;
			}

			_progress("Syncing game items...", 0.35f);
			_log("Starting game item sync via PlygroundLoader.Update...");

			ct.ThrowIfCancellationRequested();
			await PlygroundLoader.Update(info.gameItemPath, info.buildFilePath, info.modulePath, info.assetPath);
			ct.ThrowIfCancellationRequested();

			_log("Game item sync finished.");

			_progress("Saving synced assets...", 0.85f);
			AssetDatabase.SaveAssets();
			EditorSceneManager.SaveOpenScenes();

			_progress("Writing sync cache...", 0.94f);
			_cache.SaveSyncInfo(info);
			WriteCache(gameId, revision);
			EnvironmentImporter.UpdateMarker(gameId, revision, info, _log);

			_progress("Done.", 1f);
		}

		// private void UpsertSceneMarker(string gameId, string revision, SyncBuildInfo info)
		// {
		// 	if (!EnvironmentImporter.TryGetMarker(out var marker) || marker == null)
		// 	{
		// 		// If your marker is created by EnvironmentImporter, this will usually exist.
		// 		// But this is a safety net.
		// 		var go = new GameObject("PlygroundMarker");
		// 		marker = go.AddComponent<SceneMarker>();
		// 	}

		// 	marker.gameId = gameId;
		// 	marker.revision = revision ?? "unknown";
		// 	marker.localServerBaseUrl = _local?.BaseUrl;
		// 	marker.importedAtUtc = DateTime.UtcNow.ToString("o");

		// 	// Paths from /sync/list (THIS was missing)
		// 	marker.syncRootPath = info.path;
		// 	marker.environmentPath = info.environmentPath;
		// 	marker.gameItemPath = info.gameItemPath;
		// 	marker.buildFilePath = info.buildFilePath;
		// 	marker.modulePath = info.modulePath;
		// 	marker.assetPath = info.assetPath;

		// 	EditorUtility.SetDirty(marker);
		// }

		private static string[] ExtractModuleIds(BuildJson buildJson)
		{
			if (buildJson == null) return Array.Empty<string>();

			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			AddRange(ids, new string[] { buildJson.selectedGame });
			AddRange(ids, buildJson.selectedCharacters);
			AddRange(ids, buildJson.selectedNature);
			AddRange(ids, buildJson.selectedProps);
			AddRange(ids, buildJson.selectedUserAssets);

			Add(ids, buildJson.avatar?.moduleId);

			if (buildJson.npcs != null)
			{
				foreach (var npc in buildJson.npcs)
					Add(ids, npc?.moduleId);
			}

			if (buildJson.gameFeatures != null)
			{
				foreach (var feature in buildJson.gameFeatures)
					Add(ids, feature?.moduleId);
			}

			AddStoryModules(ids, buildJson.storyMap?.gameplay);
			AddStoryModules(ids, buildJson.storyMap?.environment);
			AddStoryModules(ids, buildJson.storyMap?.characters);
			AddStoryModules(ids, buildJson.storyMap?.avatar);
			AddStoryModules(ids, buildJson.storyMap?.vegetation);
			AddStoryModules(ids, buildJson.storyMap?.props);

			return ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		private static void AddStoryModules(HashSet<string> ids, BuildStoryMapSection section)
		{
			if (section == null) return;
			AddRange(ids, section.modules);
		}

		private static void AddRange(HashSet<string> ids, IEnumerable<string> values)
		{
			if (values == null) return;
			foreach (var v in values) Add(ids, v);
		}

		private static void Add(HashSet<string> ids, string value)
		{
			if (ids == null) return;
			if (string.IsNullOrWhiteSpace(value)) return;
			ids.Add(value.Trim());
		}

		private static void OpenMainScene(Action<string> log)
		{
			log?.Invoke("Refreshing AssetDatabase before main scene lookup...");
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

			var activeScene = EditorSceneManager.GetActiveScene();
			log?.Invoke($"Active scene before switch: name='{activeScene.name}' path='{activeScene.path}'");
			if (IsPreferredMainSceneName(activeScene.name))
			{
				log?.Invoke($"Main scene already open: {activeScene.path}");
				return;
			}

			var guids = AssetDatabase.FindAssets("t:Scene");
			if (guids == null || guids.Length == 0)
				throw new Exception("Could not find any scene assets after installing packages.");
			log?.Invoke($"Scene search found {guids.Length} scene asset(s).");

			var candidateScenePaths = guids
				.Select(AssetDatabase.GUIDToAssetPath)
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Where(path => IsPreferredMainSceneName(System.IO.Path.GetFileNameWithoutExtension(path)))
				.OrderBy(path => GetMainScenePriority(path))
				.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			log?.Invoke($"Main-scene candidate count: {candidateScenePaths.Length}");
			foreach (var candidateScenePath in candidateScenePaths.Take(5))
				log?.Invoke($"Main-scene candidate: {candidateScenePath}");
			if (candidateScenePaths.Length == 0)
			{
				var knownScenePaths = guids
					.Select(AssetDatabase.GUIDToAssetPath)
					.Where(path => !string.IsNullOrWhiteSpace(path))
					.ToArray();

				foreach (var knownScenePath in knownScenePaths.Take(20))
					log?.Invoke($"Known scene asset: {knownScenePath}");

				if (knownScenePaths.Length == 1)
				{
					var fallbackScenePath = knownScenePaths[0];
					log?.Invoke($"Falling back to the only available scene asset: {fallbackScenePath}");
					EditorSceneManager.OpenScene(fallbackScenePath, OpenSceneMode.Single);
					log?.Invoke($"Opened fallback scene: {fallbackScenePath}");
					return;
				}
			}

			var scenePath = candidateScenePaths.FirstOrDefault();

			if (string.IsNullOrWhiteSpace(scenePath))
				throw new Exception("Could not find a scene named 'MainScene' or 'main' after installing packages.");

			EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
			log?.Invoke($"Opened main scene: {scenePath}");
		}

		private static bool IsPreferredMainSceneName(string sceneName)
		{
			return string.Equals(sceneName, "MainScene", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sceneName, "main", StringComparison.OrdinalIgnoreCase);
		}

		private static int GetMainScenePriority(string path)
		{
			if (path.StartsWith("Assets/plyground/", StringComparison.OrdinalIgnoreCase))
				return 0;

			var sceneName = System.IO.Path.GetFileNameWithoutExtension(path);
			if (string.Equals(sceneName, "MainScene", StringComparison.OrdinalIgnoreCase))
				return 1;

			if (string.Equals(sceneName, "main", StringComparison.OrdinalIgnoreCase))
				return 2;

			return 3;
		}

		private string ResolveRevision(SyncBuildInfo info)
		{
			if (info == null || string.IsNullOrWhiteSpace(info.buildFilePath))
				return "unknown";

			if (!PathJsonLoader.TryLoadJsonFile<BuildJson>(info.buildFilePath, out var buildJson) || buildJson == null)
				return "unknown";

			return string.IsNullOrWhiteSpace(buildJson.revision) ? "unknown" : buildJson.revision;
		}

		private void WriteCache(string gameId, string revision)
		{
			_cache.Write(new PlysyncCache
			{
				gameId = gameId,
				lastImportedRevision = revision,
				lastImportedAtUtc = DateTime.UtcNow.ToString("o")
			});
		}

		private static PackagesBlock MergePackages(PackagesBlock a, PackagesBlock b)
		{
			if (a == null || a.value == null) return b;
			if (b == null || b.value == null) return a;

			return new PackagesBlock
			{
				value = a.value.Concat(b.value).Distinct().ToArray(),
			};
		}

		private static UpmPackage[] MergeUpm(UpmPackage[] a, UpmPackage[] b)
		{
			var all = new List<UpmPackage>();
			if (a != null) all.AddRange(a.Where(x => x != null));
			if (b != null) all.AddRange(b.Where(x => x != null));

			return all
				.GroupBy(x => !string.IsNullOrWhiteSpace(x.name) ? ("name:" + x.name) : ("git:" + (x.git ?? "")), StringComparer.OrdinalIgnoreCase)
				.Select(g => g.Last())
				.ToArray();
		}

		private static AssetStoreReq[] MergeAssetStore(AssetStoreReq[] a, AssetStoreReq[] b)
		{
			var all = new List<AssetStoreReq>();
			if (a != null) all.AddRange(a.Where(x => x != null));
			if (b != null) all.AddRange(b.Where(x => x != null));

			return all
				.GroupBy(x => x.productId)
				.Select(g => g.Last())
				.ToArray();
		}
	}
}
#endif
