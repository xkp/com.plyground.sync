#if UNITY_EDITOR
using Plysync.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Plyground.Sync.Runtime;

namespace Plyground.Editor
{
	public static class EnvironmentImporter
	{
		private const string RootOut = "Assets/Plyground/Imported";
		private const string MarkerName = "PlygroundMarker";

		public static void Import(
			string gameId,
			string revision,
			string outputFolder,
			SyncBuildInfo info,
			IList<PostProcessNode> postProcess,
			Action<string> log)
		{
			log ??= (_) => { };
			if (string.IsNullOrWhiteSpace(gameId)) throw new Exception("gameId is required");
			if (info == null) throw new Exception("SyncBuildInfo is null");

			// Resolve input folder from environmentPath
			var envPath = info.environmentPath;
			if (string.IsNullOrWhiteSpace(envPath))
				throw new Exception("SyncBuildInfo.environmentPath is required");

			var inputFolder = ResolveInputFolder(envPath);
			if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
				throw new Exception($"Environment input folder not found: {inputFolder} (from {envPath})");

			// Output folder inside Assets
			var safeId = Sanitize(gameId);
			outputFolder = Path.Combine(outputFolder, "plyground", "Environment").Replace("\\", "/");

			EnsureAssetFolder("Assets/plyground/Environment");

			log($"Environment import:");
			log($"  input : {inputFolder}");
			log($"  output: {outputFolder}");

			// ?? Your loader entry point
			ThreedeeLoader.Load(
				inputFolder,
				outputFolder,
				postProcess ?? new List<PostProcessNode>()
			);

			// Mark scene as linked/imported so startup can distinguish
			// imported projects from first-run import candidates.
			EnsureMarker(gameId, revision, info, log);

			EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
		}

		public static bool TryGetMarker(out SceneMarker marker)
		{
			marker = UnityEngine.Object.FindFirstObjectByType<SceneMarker>();
			return marker != null;
		}

		private static void EnsureMarker(string gameId, string revision, SyncBuildInfo info, Action<string> log)
		{
			if (!TryGetMarker(out var marker))
			{
				var go = new GameObject(MarkerName);
				marker = go.AddComponent<SceneMarker>();
				log?.Invoke("Created Plyground marker.");
			}

			marker.gameId = gameId;
			marker.revision = revision ?? "unknown";

			// Persist sync/list data so we can operate offline
			marker.syncRootPath = info.path;
			marker.environmentPath = info.environmentPath;
			marker.gameItemPath = info.gameItemPath;
			marker.buildFilePath = info.buildFilePath;
			marker.modulePath = info.modulePath;
			marker.assetPath = info.assetPath;
			marker.importedAtUtc = DateTime.UtcNow.ToString("o");

			EditorUtility.SetDirty(marker);
		}

		private static string ResolveInputFolder(string environmentPath)
		{
			if (Directory.Exists(environmentPath))
				return environmentPath;

			var dir = Path.GetDirectoryName(environmentPath);
			return string.IsNullOrWhiteSpace(dir) ? environmentPath : dir;
		}

		private static void ClearSceneButKeepMarker()
		{
			// Create new empty scene (single-mode)
			var scenePath = EditorSceneManager.GetActiveScene().path;
			var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			if (!string.IsNullOrWhiteSpace(scenePath))
				EditorSceneManager.SaveScene(newScene, scenePath);
		}

		private static void EnsureAssetFolder(string folder)
		{
			var parts = folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0 || parts[0] != "Assets")
				throw new Exception("Output folder must be under Assets/");

			var current = "Assets";
			for (int i = 1; i < parts.Length; i++)
			{
				var next = $"{current}/{parts[i]}";
				if (!AssetDatabase.IsValidFolder(next))
					AssetDatabase.CreateFolder(current, parts[i]);
				current = next;
			}
		}

		private static string Sanitize(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "unknown";
			foreach (var c in Path.GetInvalidFileNameChars())
				s = s.Replace(c, '_');
			return s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
		}
	}
}
#endif
