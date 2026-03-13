#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;
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
		private readonly Action<string> _log;
		private readonly Action<string, float> _progress;

		public Publisher(Action<string> log, Action<string, float> progress)
		{
			_log = log;
			_progress = progress;
		}

		public async Task<string> BuildWebGLZip(string gameId, string revision, bool developmentBuild, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();

			// Collect enabled scenes
			var scenes = EditorBuildSettings.scenes;
			var enabledScenes = Array.FindAll(scenes, s => s.enabled).Select(s => s.path).ToArray();
			if (enabledScenes.Length == 0)
				throw new Exception("No enabled scenes in Build Settings. Add your main scene(s).");

			// Output folders
			var baseOut = Path.Combine("Temp", "PlysyncBuilds", gameId, revision ?? "rev");
			var buildDir = Path.Combine(baseOut, "WebGL");
			var zipPath = Path.Combine(baseOut, $"WebGL_{gameId}_{revision}.zip");

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

			// Build is synchronous; wrap so UI stays responsive.
			await Task.Run(() =>
			{
				var report = BuildPipeline.BuildPlayer(opts);
				if (report.summary.result != BuildResult.Succeeded)
					throw new Exception($"Build failed: {report.summary.result} errors={report.summary.totalErrors}");
			}, ct);

			ct.ThrowIfCancellationRequested();

			_progress("Zipping build...", 0.80f);
			if (File.Exists(zipPath)) File.Delete(zipPath);

			// Zip entire buildDir contents
			ZipFile.CreateFromDirectory(buildDir, zipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
			_log($"Zip created: {zipPath}");

			return zipPath;
		}
	}
}
#endif