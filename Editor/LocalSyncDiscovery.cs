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
		private static readonly string[] EnvironmentFileNames = { "environment.json" };
		private static readonly string[] GameItemFileNames = { "gameitems.json", "gameItems.json", "items.json" };
		private static readonly string[] ModuleDirectoryNames = { "modules", "module" };
		private static readonly string[] AssetDirectoryNames = { "assets", "asset" };

		public static SyncBuildInfo[] Discover(Action<string> log)
		{
			log ??= _ => { };

			var searchRoot = GetVariantSearchRoot();
			if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
			{
				log("Variant search root was not found two levels above the Unity project.");
				return Array.Empty<SyncBuildInfo>();
			}

			var candidates = new List<SyncBuildInfo>();
			var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			log($"Inspecting variant root: {searchRoot}");

			foreach (var root in EnumerateCandidateRoots(searchRoot))
			{
				if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
				if (!seenRoots.Add(root)) continue;
				if (TryBuildInfo(root, out var info))
					candidates.Add(info);
			}

			log($"Local discovery found {candidates.Count} candidate project(s).");
			return candidates
				.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public static bool TryFindByRoot(string rootPath, out SyncBuildInfo info)
		{
			info = null;
			if (string.IsNullOrWhiteSpace(rootPath)) return false;
			if (!Directory.Exists(rootPath)) return false;
			return TryBuildInfo(rootPath, out info);
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

		private static bool TryBuildInfo(string root, out SyncBuildInfo info)
		{
			info = null;

			var environmentFile = FindFirstFile(root, EnvironmentFileNames);
			var gameItemFile = FindFirstFile(root, GameItemFileNames);
			var modulePath = FindFirstDirectory(root, ModuleDirectoryNames);
			var assetPath = FindFirstDirectory(root, AssetDirectoryNames);

			if (string.IsNullOrWhiteSpace(environmentFile) ||
				string.IsNullOrWhiteSpace(gameItemFile) ||
				string.IsNullOrWhiteSpace(modulePath) ||
				string.IsNullOrWhiteSpace(assetPath))
				return false;

			info = new SyncBuildInfo
			{
				name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
				path = root,
				environmentPath = environmentFile,
				gameItemPath = gameItemFile,
				buildFilePath = FindFirstFile(root, new[] { "build.json" }),
				modulePath = modulePath,
				assetPath = assetPath
			};

			return true;
		}

		private static string FindFirstFile(string root, IEnumerable<string> fileNames)
		{
			foreach (var fileName in fileNames)
			{
				var direct = Path.Combine(root, fileName);
				if (File.Exists(direct)) return direct;

				var nested = FindInImmediateChildren(root, fileName, expectDirectory: false);
				if (!string.IsNullOrWhiteSpace(nested)) return nested;
			}

			return null;
		}

		private static string FindFirstDirectory(string root, IEnumerable<string> directoryNames)
		{
			foreach (var directoryName in directoryNames)
			{
				var direct = Path.Combine(root, directoryName);
				if (Directory.Exists(direct)) return direct;

				var nested = FindInImmediateChildren(root, directoryName, expectDirectory: true);
				if (!string.IsNullOrWhiteSpace(nested)) return nested;
			}

			return null;
		}

		private static string FindInImmediateChildren(string root, string name, bool expectDirectory)
		{
			string[] children;
			try
			{
				children = Directory.GetDirectories(root);
			}
			catch
			{
				return null;
			}

			foreach (var child in children)
			{
				var path = Path.Combine(child, name);
				if (expectDirectory && Directory.Exists(path)) return path;
				if (!expectDirectory && File.Exists(path)) return path;
			}

			return null;
		}
	}
}
#endif
