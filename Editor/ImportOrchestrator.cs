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
			_local = local;
			_cache = cache;
			_log = log ?? (_ => { });
			_progress = progress ?? ((_, __) => { });
		}

		public async Task Run(SyncBuildInfo info, CancellationToken ct)
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

				var resolvedPackages = await _local.ResolvePackagesForModules(moduleIds, ct);
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

			// Revision-based cache short-circuit (only if we actually have a revision)
			var cached = _cache.Read(gameId);
			if (cached != null && revision != "unknown" && cached.lastImportedRevision == revision)
			{
				_log($"No changes. revision={revision}");
				// still ensure marker has the latest paths
				UpsertSceneMarker(gameId, revision, info);
				return;
			}

			// Packages (optional)
			if (packages != null)
			{
				_progress("Installing packages...", 0.15f);
				await PackageInstaller.Install(packages, _log, ct);
			}
			else
			{
				_log("No packages block (or build.json missing). Skipping package install.");
			}

			// Environment import (ThreedeeLoader)
			_progress("Importing environment (ThreedeeLoader)...", 0.55f);
			string outputFolder = Application.dataPath;
			EnvironmentImporter.Import(
				gameId,
				revision,
				outputFolder,
				info,
				_defaultPostProcess,
				_log
			);

			// Game items import (BigGameLoader)
			_progress("Importing game items (BigGameLoader)...", 0.72f);

			var preprocess = new List<PostProcessNode>(); // keep empty for now, wire UI later if needed
			await PlygroundLoader.Load(
				info.gameItemPath,
				info.buildFilePath,
				info.modulePath,
				info.assetPath,
				preprocess
			);

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
			UpsertSceneMarker(gameId, revision, info);

			_progress("Done.", 1f);
		}

		private void UpsertSceneMarker(string gameId, string revision, SyncBuildInfo info)
		{
			if (!EnvironmentImporter.TryGetMarker(out var marker) || marker == null)
			{
				// If your marker is created by EnvironmentImporter, this will usually exist.
				// But this is a safety net.
				var go = new GameObject("PlygroundMarker");
				marker = go.AddComponent<SceneMarker>();
			}

			marker.gameId = gameId;
			marker.revision = revision ?? "unknown";
			marker.localServerBaseUrl = _local?.BaseUrl;
			marker.importedAtUtc = DateTime.UtcNow.ToString("o");

			// Paths from /sync/list (THIS was missing)
			marker.syncRootPath = info.path;
			marker.environmentPath = info.environmentPath;
			marker.gameItemPath = info.gameItemPath;
			marker.buildFilePath = info.buildFilePath;
			marker.modulePath = info.modulePath;
			marker.assetPath = info.assetPath;

			EditorUtility.SetDirty(marker);
		}

		private static string[] ExtractModuleIds(BuildJson buildJson)
		{
			if (buildJson == null) return Array.Empty<string>();

			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
