#if UNITY_EDITOR
using System;

namespace Plysync.Editor
{
	// Returned by GET /sync/list
	[Serializable]
	public class SyncBuildInfo
	{
		public string name;
		public string path;
		public string environmentPath;
		public string gameItemPath;
		public string buildFilePath;
		public string modulePath;
		public string assetPath;
	}

	// Environment + items are loaded from the *paths* above.
	// These must match your actual JSON files at environmentPath/gameItemPath.
	[Serializable]
	public class EnvironmentData
	{
		public int schemaVersion;
		public string sceneName;
		public int seed;
		public Placement[] placements;
	}

	[Serializable]
	public class Placement
	{
		public string id;
		public string tag;
		public string assetRef;
		public float[] pos;
		public float[] rot;
		public float[] scale;
	}

	[Serializable]
	public class ItemsData
	{
		public int schemaVersion;
		public GameItem[] list;
	}

	[Serializable]
	public class GameItem
	{
		public string id;
		public string type;
		public string assetRef;
		public string jsonConfig;
	}

	// OPTIONAL: only if you decide to parse build.json for revision/packages
	[Serializable]
	public class BuildJson
	{
		// Legacy / optional fields
		public string revision;
		public PackagesBlock packages;

		// Module selections (used to resolve packages from the local Plyground service)
		public string selectedGame;
		public string[] selectedCharacters;
		public string[] selectedNature;
		public string[] selectedProps;
		public string[] selectedUserAssets;
		public BuildAvatar avatar;
		public BuildNpc[] npcs;
		public BuildFeature[] gameFeatures;
		public BuildStoryMap storyMap;
	}

	[Serializable]
	public class BuildAvatar
	{
		public string moduleId;
	}

	[Serializable]
	public class BuildNpc
	{
		public string moduleId;
	}

	[Serializable]
	public class BuildFeature
	{
		public string moduleId;
	}

	[Serializable]
	public class BuildStoryMap
	{
		public BuildStoryMapSection gameplay;
		public BuildStoryMapSection environment;
		public BuildStoryMapSection characters;
		public BuildStoryMapSection avatar;
		public BuildStoryMapSection vegetation;
		public BuildStoryMapSection props;
	}

	[Serializable]
	public class BuildStoryMapSection
	{
		public string[] modules;
	}

	[Serializable]
	public class PackagesBlock
	{
		public string[] value;
	}

	[Serializable]
	public class UpmPackage { public string name; public string git; public string version; }
	[Serializable]
	public class AssetStoreReq { public int productId; public string name; public string version; }

	[Serializable]
	public class ModulePackagesResolveRequest
	{
		public string[] moduleIds;
	}
}
#endif
