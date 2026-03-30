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

		private static string GetProjectRootAbsolutePath()
		{
			return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
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
