#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace Plysync.Editor
{
	internal static class ProjectVersionUpdater
	{
		private const string ProjectVersionFileName = "ProjectVersion.txt";
		private const string EditorVersionPrefix = "m_EditorVersion:";
		private const string EditorVersionWithRevisionPrefix = "m_EditorVersionWithRevision:";

		public static void EnsureCurrentEditorVersion(Action<string> log)
		{
			try
			{
				var projectSettingsPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings"));
				var projectVersionPath = Path.Combine(projectSettingsPath, ProjectVersionFileName);
				var currentVersion = Application.unityVersion;

				string existingRevisionLine = null;
				if (File.Exists(projectVersionPath))
				{
					foreach (var line in File.ReadAllLines(projectVersionPath))
					{
						if (line.StartsWith(EditorVersionWithRevisionPrefix, StringComparison.Ordinal))
						{
							existingRevisionLine = line;
							break;
						}
					}
				}

				Directory.CreateDirectory(projectSettingsPath);

				using (var writer = new StreamWriter(projectVersionPath, false))
				{
					writer.WriteLine($"{EditorVersionPrefix} {currentVersion}");
					if (!string.IsNullOrWhiteSpace(existingRevisionLine))
						writer.WriteLine(existingRevisionLine);
				}

				log?.Invoke($"Updated ProjectSettings/{ProjectVersionFileName} to editor version {currentVersion}.");
			}
			catch (Exception ex)
			{
				log?.Invoke($"Failed to update ProjectSettings/{ProjectVersionFileName}: {ex.Message}");
			}
		}
	}
}
#endif
