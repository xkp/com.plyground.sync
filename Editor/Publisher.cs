#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Plysync.Editor
{
	public sealed class Publisher
	{
		private sealed class ResourceBucket
		{
			public int count;
			public long bytes;
		}

		private sealed class ResourceEntry
		{
			public string path;
			public long bytes;
		}

		private readonly Action<string> _log;
		private readonly Action<string, float> _progress;

		public Publisher(Action<string> log, Action<string, float> progress)
		{
			_log = log;
			_progress = progress;
		}

		public async Task<string> BuildWebGL(string gameId, string revision, bool developmentBuild, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();

			// Collect enabled scenes
			var scenes = EditorBuildSettings.scenes;
			var enabledScenes = Array.FindAll(scenes, s => s.enabled).Select(s => s.path).ToArray();
			if (enabledScenes.Length == 0)
				throw new Exception("No enabled scenes in Build Settings. Add your main scene(s).");

			// Output folders: UnityProject/Build alongside Assets/
			var safeGameId = SanitizePathSegment(string.IsNullOrWhiteSpace(gameId) ? "game" : gameId);
			var safeRevision = SanitizePathSegment(string.IsNullOrWhiteSpace(revision) ? "rev" : revision);
			var projectRoot = GetProjectRootAbsolutePath();
			var buildDir = Path.Combine(projectRoot, "Build");

			if (Directory.Exists(buildDir))
				Directory.Delete(buildDir, recursive: true);
			Directory.CreateDirectory(buildDir);

			// Switch target to WebGL
			_progress("Switching build target to WebGL...", 0.40f);
			if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
			{
				var ok = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
				if (!ok) throw new Exception("Failed to switch build target to WebGL.");
			}

			// Configure options
			var opts = new BuildPlayerOptions
			{
				scenes = enabledScenes,
				locationPathName = buildDir,
				target = BuildTarget.WebGL,
				options = developmentBuild ? BuildOptions.Development : BuildOptions.None
			};

			_log($"Building WebGL to: {buildDir}");
			_progress("Running build pipeline...", 0.55f);

			// Unity build APIs should run on the editor thread.
			await Task.Yield();
			var report = BuildPipeline.BuildPlayer(opts);
			if (report.summary.result != BuildResult.Succeeded)
				throw new Exception($"Build failed: {report.summary.result} errors={report.summary.totalErrors}");

			ct.ThrowIfCancellationRequested();
			_progress("WebGL build complete.", 0.80f);
			_log($"WebGL build created: {buildDir}");
			return buildDir;
		}

		public string CollectWebGLResourceSummary(CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();

			var scenes = EditorBuildSettings.scenes;
			var enabledScenes = Array.FindAll(scenes, s => s.enabled).Select(s => s.path).ToArray();
			if (enabledScenes.Length == 0)
				throw new Exception("No enabled scenes in Build Settings. Add your main scene(s).");

			_progress("Collecting scene dependencies...", 0.15f);
			var dependencies = AssetDatabase
				.GetDependencies(enabledScenes, true)
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var byExtension = new System.Collections.Generic.Dictionary<string, ResourceBucket>(StringComparer.OrdinalIgnoreCase);
			var byRoot = new System.Collections.Generic.Dictionary<string, ResourceBucket>(StringComparer.OrdinalIgnoreCase);
			var largestAssets = new System.Collections.Generic.List<ResourceEntry>();
			long totalBytes = 0;
			int sizedAssetCount = 0;
			int unresolvedAssetCount = 0;

			_progress("Measuring asset sizes...", 0.45f);
			for (int i = 0; i < dependencies.Length; i++)
			{
				ct.ThrowIfCancellationRequested();

				var assetPath = dependencies[i];
				var size = TryGetAssetSize(assetPath, out var bytes) ? bytes : -1;
				var extension = GetExtensionLabel(assetPath);
				var root = GetRootBucket(assetPath);

				IncrementBucket(byExtension, extension, size);
				IncrementBucket(byRoot, root, size);

				if (size >= 0)
				{
					totalBytes += size;
					sizedAssetCount++;
					largestAssets.Add(new ResourceEntry { path = assetPath, bytes = size });
				}
				else
				{
					unresolvedAssetCount++;
				}

				if ((i % 50) == 0)
					_progress("Measuring asset sizes...", 0.45f + (0.35f * (i / (float)Math.Max(1, dependencies.Length))));
			}

			var topExtensions = byExtension
				.OrderByDescending(kvp => kvp.Value.bytes)
				.ThenByDescending(kvp => kvp.Value.count)
				.Take(12)
				.ToArray();

			var topRoots = byRoot
				.OrderByDescending(kvp => kvp.Value.bytes)
				.ThenByDescending(kvp => kvp.Value.count)
				.Take(12)
				.ToArray();

			var topAssets = largestAssets
				.OrderByDescending(x => x.bytes)
				.Take(15)
				.ToArray();

			_progress("Formatting resource summary...", 0.90f);

			var sb = new System.Text.StringBuilder();
			sb.AppendLine("WebGL Resource Summary");
			sb.AppendLine($"Enabled scenes: {enabledScenes.Length}");
			sb.AppendLine($"Referenced assets: {dependencies.Length}");
			sb.AppendLine($"Assets with measured size: {sizedAssetCount}");
			sb.AppendLine($"Assets with unresolved size: {unresolvedAssetCount}");
			sb.AppendLine($"Estimated total referenced size: {FormatBytes(totalBytes)}");
			sb.AppendLine();
			sb.AppendLine("Largest asset types:");
			foreach (var bucket in topExtensions)
				sb.AppendLine($"- {bucket.Key}: {FormatBytes(bucket.Value.bytes)} across {bucket.Value.count} assets");

			sb.AppendLine();
			sb.AppendLine("Largest source roots:");
			foreach (var bucket in topRoots)
				sb.AppendLine($"- {bucket.Key}: {FormatBytes(bucket.Value.bytes)} across {bucket.Value.count} assets");

			sb.AppendLine();
			sb.AppendLine("Largest referenced assets:");
			foreach (var asset in topAssets)
				sb.AppendLine($"- {FormatBytes(asset.bytes)}  {asset.path}");

			_progress("Resource summary ready.", 1f);
			_log("Collected WebGL resource summary.");
			return sb.ToString();
		}

		private static string GetProjectRootAbsolutePath()
		{
			return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		}

		private static void IncrementBucket(System.Collections.Generic.IDictionary<string, ResourceBucket> buckets, string key, long size)
		{
			if (!buckets.TryGetValue(key, out var bucket))
			{
				bucket = new ResourceBucket();
				buckets[key] = bucket;
			}

			bucket.count++;
			if (size >= 0)
				bucket.bytes += size;
		}

		private static bool TryGetAssetSize(string assetPath, out long bytes)
		{
			bytes = 0;
			try
			{
				var fullPath = Path.GetFullPath(assetPath);
				if (!File.Exists(fullPath))
					return false;

				bytes = new FileInfo(fullPath).Length;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string GetExtensionLabel(string assetPath)
		{
			var ext = Path.GetExtension(assetPath);
			return string.IsNullOrWhiteSpace(ext) ? "[no extension]" : ext.ToLowerInvariant();
		}

		private static string GetRootBucket(string assetPath)
		{
			if (string.IsNullOrWhiteSpace(assetPath))
				return "[unknown]";

			var normalized = assetPath.Replace('\\', '/');
			var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0)
				return "[unknown]";

			if (segments.Length == 1)
				return segments[0];

			return segments[0] + "/" + segments[1];
		}

		private static string FormatBytes(long bytes)
		{
			string[] suffixes = { "B", "KB", "MB", "GB" };
			double value = bytes;
			int suffixIndex = 0;
			while (value >= 1024d && suffixIndex < suffixes.Length - 1)
			{
				value /= 1024d;
				suffixIndex++;
			}

			return value >= 10d || suffixIndex == 0
				? $"{value:0} {suffixes[suffixIndex]}"
				: $"{value:0.0} {suffixes[suffixIndex]}";
		}

		private static string SanitizePathSegment(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return "value";

			var invalidChars = Path.GetInvalidFileNameChars();
			var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
			return string.IsNullOrWhiteSpace(sanitized) ? "value" : sanitized;
		}
	}
}
#endif
