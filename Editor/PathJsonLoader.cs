#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace Plysync.Editor
{
	public static class PathJsonLoader
	{
		public static T LoadJsonFile<T>(string filePath)
		{
			if (string.IsNullOrWhiteSpace(filePath))
				throw new Exception("filePath is empty");

			if (!File.Exists(filePath))
				throw new Exception($"File not found: {filePath}");

			var json = File.ReadAllText(filePath);
			if (string.IsNullOrWhiteSpace(json))
				throw new Exception($"File is empty: {filePath}");

			var obj = JsonUtility.FromJson<T>(json);
			if (obj == null)
				throw new Exception($"Failed to parse JSON: {filePath}");

			return obj;
		}

		public static bool TryLoadJsonFile<T>(string filePath, out T obj)
		{
			obj = default;
			try
			{
				obj = LoadJsonFile<T>(filePath);
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
#endif