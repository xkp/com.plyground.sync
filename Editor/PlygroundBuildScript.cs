using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System;
using System.Linq;
using UnityEngine.SceneManagement;
using Unity.AI.Navigation;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using Newtonsoft.Json.Linq;

public class PlygroundBuildScript
{
	internal static class Configuration
	{
		public static string inputFolder { get; set; }
		public static string outputFolder { get; set; }
		public static string gameItemPath { get; set; }
		public static string modulePath { get; set; }
		public static string buildFile { get; set; }
	}

	static void BindDirectories()
	{
		string[] args = System.Environment.GetCommandLineArgs();

		var i = 0;
		while (i < args.Length)
		{
			var arg = args[i];
			if (arg == "-inputFolder")
				Configuration.inputFolder = args[i + 1];
			else if (arg == "-outputFolder")
				Configuration.outputFolder = args[i + 1];
			else if (arg == "-itemFile")
				Configuration.gameItemPath = args[i + 1];
			else if (arg == "-moduleFolder")
				Configuration.modulePath = args[i + 1];
			else if (arg == "-buildFile")
				Configuration.buildFile = args[i + 1];

			i++;
		}
	}

	[Serializable]
	class BuildStepData
	{
		public List<string> dependencies; //for install step
	}

	[Serializable]
	class BuildStep
	{
		public string name;
		public string error;
		public BuildStepData data;
	}

	static class BuildState
	{
		public static List<BuildStep> steps = new List<BuildStep>();

		public static void AddError(string stepName, string message)
		{
			var step = steps.FirstOrDefault(s => s.name == stepName);
			if (step == null)
			{
				step = new BuildStep
				{
					name = stepName,
					error = message
				};
				steps.Add(step);
			}
			else
				step.error = message;
		}

		public static BuildStep GetStep(string stepName)
		{
			return steps.Find(s => s.name == stepName);
		}

		public static BuildStep Add(string stepName)
		{
			var step = new BuildStep { name = stepName, data = new BuildStepData() };
			steps.Add(step);
			return step;
		}
	}

	[Serializable]
	class ExportState
	{
		public List<BuildStep> steps;
	}

	static void BindState()
	{
		if (Directory.Exists(Configuration.inputFolder))
		{
			if (File.Exists(Configuration.buildFile))
			{
				string json = File.ReadAllText(Configuration.buildFile);
				var import = JsonUtility.FromJson<ExportState>(json);
				BuildState.steps = import.steps;
			}
		}
		else
		{
			BuildState.steps.Clear();
		}
	}

	private static void SaveState()
	{
		if (Directory.Exists(Configuration.inputFolder))
		{
			string json = JsonUtility.ToJson(new ExportState { steps = BuildState.steps });
			Debug.Log($"[threedee] Writing: {Configuration.buildFile} with contents: {json}");

			File.WriteAllText(Configuration.buildFile, json);
		}
	}

	static string CreateStep = "create";
	static string InstallStep = "install";

	public static void Create()
	{
		Debug.Log($"[threedee] calling Create");

		BindDirectories();
		BindState();

		try
		{
			//TODO: detect our step and make sure we can move forward (i.e check dependencies)
			/*			if (BuildState.steps.Any())
						{
							Console.WriteLine($"Create has already been ran for this game");
							return;
						}
			*/
			if (!File.Exists(Configuration.gameItemPath))
			{
				BuildState.AddError(CreateStep, $"Missing item file {Configuration.gameItemPath ?? string.Empty}");
				return;
			}

			//build dependencies
			Debug.Log($"[threedee] searching for dependencies...");

			var modules = LoadGameModules(Configuration.gameItemPath, Configuration.modulePath);
			var packageDependencies = new List<string>();
			var assetDependencies = new List<string>();
			foreach (var module in modules)
			{
				if (module.dependencies != null)
				{
					Debug.Log($"[threedee] found {module.dependencies.Count} dependencies on module {module.name}");
					foreach (var dependency in module.dependencies)
					{
						Debug.Log($"[threedee] processing dependency: {dependency}...");
						if (isAssetDependency(dependency))
						{
							Debug.Log($"[threedee] asset dependency found: {dependency}...");
							assetDependencies.Add(dependency);
						}
						else if (IsPackageDependency(dependency))
						{
							Debug.Log($"[threedee] package dependency found: {dependency}...");
							packageDependencies.Add(dependency);
						}
						else
						{
							Debug.Log($"[threedee] default asset dependency found: {dependency}...");
							assetDependencies.Add(dependency);
						}
					}
				}
			}

			Debug.Log($"[threedee] installing dependencies...");
			InstallUPM(packageDependencies.ToArray());
			AddPackagesToCreateStep(assetDependencies);
		}
		finally
		{
			SaveState();
		}
	}

	private static bool isAssetDependency(string dependency)
	{
		return dependency.Contains('|');
	}

	private static void AddPackagesToCreateStep(List<string> assetDependencies)
	{
		if (assetDependencies == null || !assetDependencies.Any())
		{
			Debug.Log($"[threedee] No dependency found");
			return;
		}

		var step = BuildState.GetStep(CreateStep);
		if (step == null)
		{
			step = BuildState.Add(CreateStep);
		}

		if (step.data == null)
			step.data = new BuildStepData();

		step.data.dependencies = assetDependencies;
		if (assetDependencies?.Count > 0)
		{
			foreach (var dependency in assetDependencies)
			{
				Debug.Log($"[threedee] Dependency found: {dependency}");
			}

			step.error = "Dependencies must be installed before continuing";
		}
	}

	private const int DefaultPerPackageTimeoutSec = 600; // 10 minutes per package
	private static readonly List<(string id, AddRequest request, DateTime started)> _active = new();
	private static readonly List<(string id, string error)> _failed = new();
	private static readonly List<string> _succeeded = new();
	private static readonly List<string> _excludedBuildFiles = new();
	private static int _timeoutPerPkgSec = DefaultPerPackageTimeoutSec;
	private static bool _started = false;
	private static void InstallUPM(string[] packages)
	{
		try
		{
			if (_started)
			{
				RemoveExcludedBuildFiles();
				return;
			}
			_started = true;

			if (packages.Length == 0)
			{
				Debug.Log("[BatchAddUpmPackages] No packages specified.");
				RemoveExcludedBuildFiles();
				return;
			}

			Debug.Log($"[BatchAddUpmPackages] Installing {packages.Length} package(s) (timeout {_timeoutPerPkgSec}s each) …");

			foreach (var id in packages)
				QueueAdd(id);

			// Poll until all requests complete
			EditorApplication.update += Tick;
		}
		catch (Exception ex)
		{
			Debug.LogError($"[BatchAddUpmPackages] Failed to start: {ex}");
			EditorApplication.Exit(1);
		}
	}

	private static void QueueAdd(string id)
	{
		try
		{
			// Unity will ignore duplicates already in manifest.json
			var req = Client.Add(id);
			_active.Add((id, req, DateTime.UtcNow));
			Debug.Log($"[BatchAddUpmPackages] Add queued: {id}");
		}
		catch (Exception ex)
		{
			_failed.Add((id, $"Exception while queuing add: {ex.Message}"));
			Debug.LogError($"[BatchAddUpmPackages] Queue failed: {id}\n{ex}");
		}
	}

	private static void Tick()
	{
		// Check each active request
		for (int i = _active.Count - 1; i >= 0; i--)
		{
			var (id, req, started) = _active[i];

			// Timeout?
			if ((DateTime.UtcNow - started).TotalSeconds > _timeoutPerPkgSec)
			{
				_failed.Add((id, "Timeout"));
				Debug.LogError($"[BatchAddUpmPackages] TIMEOUT installing: {id}");
				_active.RemoveAt(i);
				continue;
			}

			switch (req.Status)
			{
				case StatusCode.InProgress:
					// still working
					break;

				case StatusCode.Success:
					_succeeded.Add(id);
					Debug.Log($"[BatchAddUpmPackages] Installed: {id}");
					_active.RemoveAt(i);
					break;

				case StatusCode.Failure:
					var message = req.Error == null ? "Unknown error" : $"{req.Error.message} (code {req.Error.errorCode})";
					_failed.Add((id, message));
					Debug.LogError($"[BatchAddUpmPackages] FAILED: {id} – {message}");
					_active.RemoveAt(i);
					break;
			}
		}

		// Done?
		if (_active.Count == 0)
		{
			EditorApplication.update -= Tick;

			// Force refresh to import any newly added assets/asmdefs
			try
			{
				RemoveExcludedBuildFiles();
				AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
				AssetDatabase.SaveAssets();
			}
			catch { /* headless safe */ }

			// Summary + exit code
			if (_failed.Count == 0)
			{
				Debug.Log($"[BatchAddUpmPackages] All packages installed successfully ({_succeeded.Count}).");
				foreach (var id in _succeeded) Debug.Log($"  ✔ {id}");
				EditorApplication.Exit(0);
			}
			else
			{
				Debug.LogError($"[BatchAddUpmPackages] Completed with failures. Success: {_succeeded.Count}, Failed: {_failed.Count}");
				foreach (var id in _succeeded) Debug.Log($"  ✔ {id}");
				foreach (var (id, err) in _failed) Debug.LogError($"  ✖ {id} – {err}");
				EditorApplication.Exit(1);
			}
		}
	}
	private static bool IsPackageDependency(string dependency)
	{
		return !string.IsNullOrEmpty(dependency) && dependency.Split(".").Length > 2;
	}

	[Serializable]
	class ImportGame
	{
		public List<string> modules;
	}

	[Serializable]
	class ImportModule
	{
		public string id;
		public string name;
		public List<string> dependencies;
		public List<string> excludedBuildFiles;
		public List<ImportModuleProperty> moduleProperties;
		[NonSerialized] public string sourceDirectory;
	}

	[Serializable]
	class ImportModuleProperty
	{
		public string name;
		public string type;
		public string data;
		public string value;
	}

	private static List<ImportModule> LoadGameModules(string gameItemPath, string modulePath)
	{
		var result = new List<ImportModule>();
		var moduleIds = LoadModuleSource(gameItemPath);
		if (moduleIds != null)
		{
			foreach (var moduleId in moduleIds)
			{
				var bgmFile = Path.Combine(modulePath, moduleId, "module.bgm");
				if (File.Exists(bgmFile))
				{
					var bgmFileContents = EnsureModuleProperties(File.ReadAllText(bgmFile), bgmFile);
					var module = JsonUtility.FromJson<ImportModule>(bgmFileContents);
					if (module != null)
					{
						module.sourceDirectory = Path.GetDirectoryName(bgmFile);
						RegisterExcludedBuildFiles(module);
					}
					result.Add(module);
				}
			}
		}

		return result;
	}

	private static string EnsureModuleProperties(string moduleJson, string filePath)
	{
		if (string.IsNullOrWhiteSpace(moduleJson))
			return moduleJson;

		var moduleObject = JObject.Parse(moduleJson);
		if (moduleObject["moduleProperties"] != null)
			return moduleJson;

		moduleObject["moduleProperties"] = new JArray
		{
			new JObject
			{
				["name"] = "NewProperty",
				["type"] = "string",
				["data"] = "",
				["value"] = "value"
			}
		};

		var normalizedJson = moduleObject.ToString();
		File.WriteAllText(filePath, normalizedJson);
		return normalizedJson;
	}

	private static List<string> LoadModuleSource(string gameItemPath)
	{
		var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		AddModuleIdsFromFile(moduleIds, gameItemPath);
		AddModuleIdsFromFile(moduleIds, GetSourceBuildFilePath(gameItemPath));
		AddModuleIdsFromFile(moduleIds, Configuration.buildFile);

		return moduleIds
			.OrderBy(moduleId => moduleId, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static string GetSourceBuildFilePath(string gameItemPath)
	{
		if (string.IsNullOrWhiteSpace(gameItemPath))
			return null;

		var directory = Path.GetDirectoryName(gameItemPath);
		if (string.IsNullOrWhiteSpace(directory))
			return null;

		return Path.Combine(directory, "build.json");
	}

	private static void AddModuleIdsFromFile(HashSet<string> moduleIds, string filePath)
	{
		if (moduleIds == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
			return;

		foreach (var moduleId in PlygroundModuleExtractor.ExtractModuleIdsFromFile(filePath))
			moduleIds.Add(moduleId);
	}

	private static void RegisterExcludedBuildFiles(ImportModule module)
	{
		if (module?.excludedBuildFiles == null || string.IsNullOrEmpty(module.sourceDirectory))
			return;

		foreach (var excludedFile in module.excludedBuildFiles)
		{
			var resolvedPath = ResolveModuleFilePath(module.sourceDirectory, excludedFile);
			if (string.IsNullOrEmpty(resolvedPath))
				continue;

			if (!_excludedBuildFiles.Contains(resolvedPath))
			{
				_excludedBuildFiles.Add(resolvedPath);
				Debug.Log($"[threedee] Excluding build file: {resolvedPath}");
			}
		}
	}

	private static string ResolveModuleFilePath(string moduleDirectory, string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return null;

		var fullPath = Path.GetFullPath(Path.IsPathRooted(filePath)
			? filePath
			: Path.Combine(moduleDirectory, filePath));

		var projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
		if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
		{
			Debug.LogWarning($"[threedee] Skipping excluded build file outside project: {filePath}");
			return null;
		}

		return fullPath;
	}

	private static void RemoveExcludedBuildFiles()
	{
		if (_excludedBuildFiles.Count == 0)
			return;

		var excludedFiles = _excludedBuildFiles.ToList();
		_excludedBuildFiles.Clear();

		foreach (var path in excludedFiles)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
					Debug.Log($"[threedee] Removed excluded build file: {path}");
				}

				var metaPath = path + ".meta";
				if (File.Exists(metaPath))
				{
					File.Delete(metaPath);
					Debug.Log($"[threedee] Removed excluded build meta file: {metaPath}");
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[threedee] Failed to remove excluded build file '{path}': {ex.Message}");
			}
		}
	}

	public static async void CreateGame()
	{
		Console.WriteLine("Creating folder structure...");
		string inputFolder;
		string outputFolder;
		string gameItemPath;
		string modulePath;
		string assetPath;
		if (!Scaffold(out inputFolder, out outputFolder, out gameItemPath, out modulePath, out assetPath))
			return;

		string buildFilePath = Path.Combine(Path.GetDirectoryName(gameItemPath), "build.json");

		Console.WriteLine($"gameItemPath = {gameItemPath}");
		Console.WriteLine($"modulePath = {modulePath}");
		Console.WriteLine($"buildFilePath = {buildFilePath}");

		Console.WriteLine("Starting scene generation...");
		Scene scene = OpenMainScene();
		if (!scene.IsValid())
		{
			Console.WriteLine("There must be at least one valid scene.");
			return;
		}

		Console.WriteLine("Loading environment assets...");
		var postProcess = new List<PostProcessNode>();
		ImportEnvironmentLikeSync(inputFolder, postProcess);

		await PlygroundLoader.Load(gameItemPath, buildFilePath, modulePath, assetPath, postProcess);

		//save before generating light maps
		EditorSceneManager.SaveScene(scene);

		Console.WriteLine("Generating lightmaps...");
		GenerateLightmaps();

		UpdateNavMeshes();

		Console.WriteLine("Finishing...");
		EditorSceneManager.SaveScene(scene);

		Debug.Log("Threedee scene generation completed. Scene saved to: " + scene.path);

		// Trigger a build
		PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
		//PlayerSettings.WebGL.template = "plyground";

		string buildPath = Path.Combine(outputFolder, "Build");
		BuildPipeline.BuildPlayer(new BuildPlayerOptions
		{
			scenes = EditorBuildSettings.scenes.Select(s => s.path).ToArray(),
			//scenes = new[] { scene.path }, //todo: added scenes
			locationPathName = buildPath,
			target = BuildTarget.WebGL, // Adjust target as necessary
			options = BuildOptions.None
		});

		Debug.Log("Build completed. Build located at: " + buildPath);
	}

	private static void UpdateNavMeshes()
	{
		//build nav meshes
		NavMeshSurface[] surfaces = GameObject.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);

		// Iterate over each surface and rebuild its nav mesh
		foreach (NavMeshSurface surface in surfaces)
		{
			if (surface != null)
			{
				surface.BuildNavMesh();
			}
		}
	}

	public static async void UpdateGame()
	{
		string inputFolder;
		string outputFolder;
		string gameItemPath;
		string modulePath;
		string assetPath;
		if (!Scaffold(out inputFolder, out outputFolder, out gameItemPath, out modulePath, out assetPath))
			return;

		string buildFilePath = Path.Combine(Path.GetDirectoryName(gameItemPath), "build.json");

		Scene scene = OpenMainScene();
		if (!scene.IsValid())
		{
			Console.WriteLine("There must be at least one valid scene.");
			return;
		}

		Console.WriteLine("Loading environment assets...");
		await PlygroundLoader.Update(gameItemPath, buildFilePath, modulePath, assetPath);

		UpdateNavMeshes();

		//save before generating light maps
		EditorSceneManager.SaveScene(scene);

		// Trigger a build
		PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
		//PlayerSettings.WebGL.template = "plyground";

		string buildPath = Path.Combine(outputFolder, "Build");

		BuildPipeline.BuildPlayer(new BuildPlayerOptions
		{
			scenes = EditorBuildSettings.scenes.Select(s => s.path).ToArray(),
			//scenes = new[] { scene.path }, //todo: added scenes
			locationPathName = buildPath,
			target = BuildTarget.WebGL, // Adjust target as necessary
			options = BuildOptions.None
		});

		Debug.Log("Build completed. Build located at: " + buildPath);
	}

	private static void GenerateLightmaps()
	{
		// Create and configure lighting settings
		LightingSettings lightingSettings = new LightingSettings();
		lightingSettings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU; // Use Progressive GPU lightmapper
		lightingSettings.maxBounces = 2; // Multiplier for indirect light intensity
		lightingSettings.indirectResolution = 2.0f; // Texels per unit for indirect light
		lightingSettings.lightmapResolution = 40.0f; // Texels per unit for baked lightmaps
		lightingSettings.lightmapPadding = 4; // Padding between UV islands
		lightingSettings.filteringMode = LightingSettings.FilterMode.Auto; // Use automatic filtering
		lightingSettings.ao = true; // Enable Ambient Occlusion
		lightingSettings.aoMaxDistance = 2.0f; // Maximum distance for AO calculations
		lightingSettings.lightmapCompression = LightmapCompression.NormalQuality;

		// Assign the lighting settings to the scene
		Lightmapping.lightingSettings = lightingSettings;

		Debug.Log("Lightmap settings configured. Starting bake...");

		// Start the baking process
		if (Lightmapping.Bake())
		{
			//Lightmapping.Bake(); //bake a second time, not sure why
			Debug.Log("Lightmap baking started successfully.");
		}
		else
		{
			Debug.LogError("Lightmap baking failed to start. Check your scene setup.");
		}

	}

	private static void ImportEnvironmentLikeSync(string inputFolder, IList<PostProcessNode> postProcess)
	{
		if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
			throw new DirectoryNotFoundException("Environment input folder not found: " + inputFolder);

		var outputFolder = Path.Combine(Application.dataPath, "plyground", "Environment").Replace("\\", "/");
		EnsureAssetFolder("Assets/plyground/Environment");

		ThreedeeLoader.Load(
			inputFolder,
			outputFolder,
			postProcess ?? new List<PostProcessNode>()
		);

		EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
	}

	private static Scene OpenMainScene()
	{
		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

		var activeScene = EditorSceneManager.GetActiveScene();
		if (IsPreferredMainSceneName(activeScene.name))
			return activeScene;

		var guids = AssetDatabase.FindAssets("t:Scene");
		if (guids == null || guids.Length == 0)
			throw new Exception("Could not find any scene assets after preparing the project.");

		var candidateScenePaths = guids
			.Select(AssetDatabase.GUIDToAssetPath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Where(path => IsPreferredMainSceneName(Path.GetFileNameWithoutExtension(path)))
			.OrderBy(path => GetMainScenePriority(path))
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (candidateScenePaths.Length == 0)
		{
			var knownScenePaths = guids
				.Select(AssetDatabase.GUIDToAssetPath)
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.ToArray();

			if (knownScenePaths.Length == 1)
				return EditorSceneManager.OpenScene(knownScenePaths[0], OpenSceneMode.Single);

			throw new Exception("Could not find a scene named 'MainScene' or 'main' after preparing the project.");
		}

		return EditorSceneManager.OpenScene(candidateScenePaths[0], OpenSceneMode.Single);
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

		var sceneName = Path.GetFileNameWithoutExtension(path);
		if (string.Equals(sceneName, "MainScene", StringComparison.OrdinalIgnoreCase))
			return 1;

		if (string.Equals(sceneName, "main", StringComparison.OrdinalIgnoreCase))
			return 2;

		return 3;
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

	private static bool Scaffold(out string inputFolder, out string outputFolder, out string gameItemPath, out string modulePath, out string localAssetPath)
	{
		inputFolder = string.Empty;
		outputFolder = string.Empty;
		gameItemPath = string.Empty;
		modulePath = string.Empty;
		localAssetPath = string.Empty;

		string[] args = System.Environment.GetCommandLineArgs();

		var i = 0;
		while (i < args.Length)
		{
			var arg = args[i];
			if (arg == "-inputFolder")
				inputFolder = args[i + 1];
			else if (arg == "-outputFolder")
				outputFolder = args[i + 1];
			else if (arg == "-itemFile")
				gameItemPath = args[i + 1];
			else if (arg == "-moduleFolder")
				modulePath = args[i + 1];
			else if (arg == "-assetFolder")
				localAssetPath = args[i + 1];

			i++;
		}

		if (!Directory.Exists(inputFolder))
		{
			Console.WriteLine("Input folder does not exist: " + inputFolder);
			return false;
		}

		return !string.IsNullOrEmpty(inputFolder) && !string.IsNullOrEmpty(outputFolder);
	}


	private static void DirectoryCopy(string sourceDirName, string destDirName)
	{
		DirectoryInfo dir = new DirectoryInfo(sourceDirName);
		if (!dir.Exists)
		{
			throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDirName);
		}

		DirectoryInfo[] dirs = dir.GetDirectories();
		Directory.CreateDirectory(destDirName);

		FileInfo[] files = dir.GetFiles();
		foreach (FileInfo file in files)
		{
			string tempPath = Path.Combine(destDirName, file.Name);
			file.CopyTo(tempPath, false);
		}

		foreach (DirectoryInfo subdir in dirs)
		{
			string tempPath = Path.Combine(destDirName, subdir.Name);
			DirectoryCopy(subdir.FullName, tempPath);
		}
	}

	private static void MarkObjectAndChildrenStatic(GameObject obj)
	{
		obj.isStatic = true;

		foreach (Transform child in obj.transform)
		{
			MarkObjectAndChildrenStatic(child.gameObject);
		}
	}
}

