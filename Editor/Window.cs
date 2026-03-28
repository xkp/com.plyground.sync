#if UNITY_EDITOR
using Plyground.Editor;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	public class PlysyncWindow : EditorWindow
	{
		private const string DefaultCloudBaseUrl = "https://api.yourcloud.com"; // change
		private const string DefaultLogoAssetPath = "Assets/plyground/Editor/plysync/logo.png";

		private enum UiState
		{
			NotPlygroundProject,
			FirstRunImport,
			LinkedProject
		}

		private string _cloudBaseUrl;
		private string _cloudToken = "";

		private bool _showAdvanced;

		private string _status = "Starting...";
		private readonly StringBuilder _log = new StringBuilder();
		private Vector2 _scroll;

		private bool _busy;
		private float _progress;
		private string _step = "";
		private CancellationTokenSource _cts;

		private CloudPublishClient _cloud;
		private CacheStore _cache;

		private bool _discovered;
		private SyncBuildInfo[] _targets = Array.Empty<SyncBuildInfo>();
		private int _selectedIndex = -1;

		// Linked/offline (marker-driven)
		private SyncBuildInfo _linkedSyncInfo; // cached sync paths
		private string _linkedGameId;          // marker.gameId (we store SyncBuildInfo.path here)
		private string _linkedRevision;        // marker.revision (optional)

		private bool _devBuild = false;
		private bool _autoSyncBeforePublish = true;
		private Texture2D _logoTexture;
		private GUIStyle _headerBodyStyle;
		private GUIStyle _gamePopupStyle;

		[MenuItem("Plyground/Sync")]
		public static void Open() => GetWindow<PlysyncWindow>("plyground");

		private void OnEnable()
		{
			_cache = new CacheStore();

			_cloudBaseUrl = EditorPrefs.GetString("Plysync.CloudBaseUrl", DefaultCloudBaseUrl);
			_cloudToken = EditorPrefs.GetString("Plysync.CloudToken", _cloudToken);
			_showAdvanced = EditorPrefs.GetBool("Plysync.ShowAdvanced", false);

			_cloud = new CloudPublishClient(_cloudBaseUrl, () => _cloudToken, Log);
			_logoTexture = LoadLogoTexture();

			EditorApplication.delayCall += () =>
			{
				if (this == null) return;
				RefreshLinkedStateFromMarker();
				_ = BootstrapLocalProject();
				Repaint();
			};
		}

		private void OnDisable()
		{
			EditorPrefs.SetString("Plysync.CloudBaseUrl", _cloudBaseUrl);
			EditorPrefs.SetString("Plysync.CloudToken", _cloudToken);
			EditorPrefs.SetBool("Plysync.ShowAdvanced", _showAdvanced);

			_cts?.Cancel();
			_cts?.Dispose();
		}

		private void OnHierarchyChange()
		{
			RefreshLinkedStateFromMarker();
			Repaint();
		}

		private UiState ComputeState()
		{
			if (!string.IsNullOrWhiteSpace(_linkedGameId))
				return UiState.LinkedProject;

			return _discovered ? UiState.FirstRunImport : UiState.NotPlygroundProject;
		}

		private void RefreshLinkedStateFromMarker()
		{
			_linkedGameId = null;
			_linkedRevision = null;
			_linkedSyncInfo = null;

			if (EnvironmentImporter.TryGetMarker(out var marker) && !string.IsNullOrWhiteSpace(marker.gameId))
			{
				_linkedGameId = marker.gameId;
				_linkedRevision = marker.revision;
				_linkedSyncInfo = BuildSyncInfoFromMarker(marker) ?? _cache.LoadSyncInfo(marker.gameId);
				return;
			}

			var lastGameId = _cache.LoadLastGameId();
			if (string.IsNullOrWhiteSpace(lastGameId))
				return;

			var cachedSyncInfo = _cache.LoadSyncInfo(lastGameId);
			if (cachedSyncInfo == null)
				return;

			_linkedGameId = lastGameId;
			_linkedRevision = ResolveRevisionFromSyncInfo(cachedSyncInfo);
			_linkedSyncInfo = cachedSyncInfo;
		}

		private void OnGUI()
		{
			var state = ComputeState();

			DrawHeaderPanel(state);
			EditorGUILayout.Space(8);
			DrawTopBar();
			EditorGUILayout.Space(10);

			switch (state)
			{
				case UiState.NotPlygroundProject:
					DrawNotPlygroundProject();
					break;
				case UiState.FirstRunImport:
					DrawFirstRunImport();
					break;
				case UiState.LinkedProject:
					DrawLinkedProject();
					break;
			}

			EditorGUILayout.Space(10);
			DrawLogs();
			EditorGUILayout.Space(8);
			DrawBottomBar();
		}

		private void DrawTopBar()
		{
			if (_busy)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField(_step);
				Rect r = EditorGUILayout.GetControlRect(false, 18);
				EditorGUI.ProgressBar(r, _progress, $"{Mathf.RoundToInt(_progress * 100)}%");

				if (GUILayout.Button("Cancel", GUILayout.Width(120)))
					_cts?.Cancel();
			}
		}

		private void DrawBottomBar()
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.Label($"Status: {_status}", GUILayout.ExpandWidth(true));
				GUI.enabled = !_busy;
				if (GUILayout.Button("Clear Logs", GUILayout.Width(110))) _log.Clear();
				GUI.enabled = true;
			}
		}

		private void DrawHeaderPanel(UiState state)
		{
			EnsureHeaderStyles();

			using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
			{
				using (new EditorGUILayout.HorizontalScope())
				{
					DrawLogo();

					using (new EditorGUILayout.VerticalScope())
					{
			            GUILayout.Space(4);
						EditorGUILayout.LabelField("Welcome to the plyground", EditorStyles.boldLabel);
						EditorGUILayout.LabelField(BuildGuidanceText(state), _headerBodyStyle);
					}
				}
			}
		}

		private void DrawLogo()
		{
			const float width = 48f;
			const float height = width * 377f / 250f;
			var texture = _logoTexture ?? (Texture2D)EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image;
			var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
			GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
			GUILayout.Space(8);
		}

		private static Texture2D LoadLogoTexture()
		{
			var direct = AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultLogoAssetPath);
			if (direct != null) return direct;

			var guids = AssetDatabase.FindAssets("logo t:Texture2D", new[] { "Assets/plyground/Editor/plysync" });
			if (guids == null || guids.Length == 0) return null;

			var path = AssetDatabase.GUIDToAssetPath(guids[0]);
			return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
		}

		private string BuildGuidanceText(UiState state)
		{
			if (_busy)
				return $"Current state: {_step}. What to do: wait for this operation to complete, or click Cancel if needed.";

			switch (state)
			{
				case UiState.NotPlygroundProject:
					return "Current state: no valid Plyground payload was found two levels above this Unity project. What to do: verify this is a generated Plyground project if you expected import support.";
				case UiState.FirstRunImport:
					return "Current state: this looks like a generated Plyground project that has not been imported yet. What to do: import it now.";
				case UiState.LinkedProject:
					return "Current state: this is an imported Plyground project. What to do: sync from disk if needed, or Build & Publish to ship a WebGL build.";
				default:
					return $"Current state: {_status}. What to do: follow the action buttons below.";
			}
		}

		private void EnsureHeaderStyles()
		{
			if (_headerBodyStyle != null) return;

			_headerBodyStyle = new GUIStyle(EditorStyles.label)
			{
				wordWrap = true
			};
		}

		private void EnsureGamePopupStyle()
		{
			if (_gamePopupStyle != null) return;

			_gamePopupStyle = new GUIStyle(EditorStyles.popup)
			{
				fontSize = 12,
				fixedHeight = 28f
			};
			_gamePopupStyle.padding = new RectOffset(10, 24, 10, 10);
		}

		private void DrawNotPlygroundProject()
		{
			EditorGUILayout.HelpBox(
				"Not a Plyground project.\n\n" +
				"No complete payload was found two levels above this Unity project.",
				MessageType.Warning
			);

			GUI.enabled = !_busy;
			if (GUILayout.Button("Rescan", GUILayout.Height(34)))
				_ = DiscoverTargets();
			GUI.enabled = true;

			DrawAdvancedFoldout();
		}

		private void DrawFirstRunImport()
		{
			EditorGUILayout.LabelField("Import Plyground Project", EditorStyles.boldLabel);

			if (!_discovered)
			{
				EditorGUILayout.HelpBox("No valid Plyground payload is available.", MessageType.Warning);
				if (GUILayout.Button("Rescan", GUILayout.Height(28)))
					_ = DiscoverTargets();
				return;
			}

			if (_targets == null || _targets.Length == 0)
			{
				EditorGUILayout.HelpBox("No local project candidates were found.", MessageType.Info);
				if (GUILayout.Button("Rescan", GUILayout.Height(28)))
					_ = DiscoverTargets();
				return;
			}

			if (_targets.Length == 1)
			{
				_selectedIndex = 0;
				EditorGUILayout.HelpBox($"Found payload: {_targets[0].name}", MessageType.None);
			}
			else
			{
				EditorGUILayout.HelpBox("Multiple valid payloads were found. Select one to import.", MessageType.Info);
				string[] labels = _targets.Select(t => t.name).ToArray();
				_selectedIndex = Mathf.Clamp(_selectedIndex, 0, Math.Max(0, labels.Length - 1));
				EnsureGamePopupStyle();
				_selectedIndex = EditorGUILayout.Popup(_selectedIndex, labels, _gamePopupStyle, GUILayout.Height(40));
			}

			EditorGUILayout.Space(8);

			using (new EditorGUILayout.HorizontalScope())
			{
				GUI.enabled = !_busy && _selectedIndex >= 0;
				if (GUILayout.Button("Import Selected", GUILayout.Height(34)))
					_ = ImportSelectedTarget();
				GUI.enabled = true;

				GUI.enabled = !_busy;
				if (GUILayout.Button("Rescan", GUILayout.Height(34), GUILayout.Width(110)))
					_ = DiscoverTargets();
				GUI.enabled = true;
			}
		}

		private void DrawLinkedProject()
		{
			if (_linkedSyncInfo == null)
			{
				EditorGUILayout.HelpBox(
					"Cached sync info (paths) not found for this marker.\n" +
					"Rescan and re-import, or ensure CacheStore.SaveSyncInfo(...) is called during import.",
					MessageType.Warning
				);
			}

			EditorGUILayout.HelpBox(
				"Imported Plyground project. It may or may not need syncing depending on whether the source payload changed.",
				MessageType.Info
			);

			EditorGUILayout.LabelField("Sync", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox("Sync reloads the detected local Plyground files from disk.", MessageType.None);

			using (new EditorGUILayout.HorizontalScope())
			{
				GUI.enabled = !_busy;
				if (GUILayout.Button("Sync Now", GUILayout.Height(28)))
					_ = SyncLinkedGame();
				GUI.enabled = true;
			}

			EditorGUILayout.Space(10);
			EditorGUILayout.LabelField("Publish (WebGL)", EditorStyles.boldLabel);

			_devBuild = EditorGUILayout.Toggle("Development Build", _devBuild);
			_autoSyncBeforePublish = EditorGUILayout.Toggle("Sync before publish", _autoSyncBeforePublish);

			if (string.IsNullOrWhiteSpace(_cloudToken))
				EditorGUILayout.HelpBox("Cloud token is empty. Set it in Advanced.", MessageType.Warning);

			GUI.enabled = !_busy && !string.IsNullOrWhiteSpace(_cloudToken);
			if (GUILayout.Button("Build & Publish", GUILayout.Height(30)))
				_ = PublishLinkedGame();
			GUI.enabled = true;
		}

		private void DrawAdvancedFoldout()
		{
			_showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
			if (!_showAdvanced) return;

			EditorGUI.indentLevel++;
			_cloudBaseUrl = EditorGUILayout.TextField("Cloud API", _cloudBaseUrl);
			_cloudToken = EditorGUILayout.PasswordField("Cloud Token", _cloudToken);

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("Reset defaults", GUILayout.Width(140)))
				{
					_cloudBaseUrl = DefaultCloudBaseUrl;
				}

				if (GUILayout.Button("Save", GUILayout.Width(100)))
				{
					EditorPrefs.SetString("Plysync.CloudBaseUrl", _cloudBaseUrl);
					EditorPrefs.SetString("Plysync.CloudToken", _cloudToken);
					EditorPrefs.SetBool("Plysync.ShowAdvanced", _showAdvanced);
					Log("Advanced settings saved.");
				}
			}
			EditorGUI.indentLevel--;
		}

		private void DrawLogs()
		{
			EditorGUILayout.LabelField("Logs", EditorStyles.boldLabel);
			_scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
			EditorGUILayout.TextArea(_log.ToString(), GUILayout.ExpandHeight(true));
			EditorGUILayout.EndScrollView();
		}

		// -------------------------
		// Actions
		// -------------------------

		private async Task BootstrapLocalProject()
		{
			if (_busy) return;
			if (string.IsNullOrWhiteSpace(_cloudBaseUrl)) _cloudBaseUrl = DefaultCloudBaseUrl;

			_cloudBaseUrl = (_cloudBaseUrl ?? "").Trim().TrimEnd('/');
			_cloud = new CloudPublishClient(_cloudBaseUrl, () => _cloudToken, Log);

			await DiscoverTargets();

			if (!string.IsNullOrWhiteSpace(_linkedGameId))
			{
				Log($"Linked project detected: {_linkedGameId}");
				ScaffoldInboxCheck();
				return;
			}

			if (_targets.Length == 1)
			{
				_selectedIndex = 0;
				Log($"Unimported Plyground project detected: {_targets[0].name}. Waiting for user to start import.");
			}
			else if (_targets.Length > 1)
			{
				Log("Multiple local project candidates found. Select one to import.");
			}
		}

		private Task DiscoverTargets()
		{
			if (_busy) return Task.CompletedTask;

			try
			{
				_status = "Scanning two levels up for variant payloads...";
				_targets = LocalSyncDiscovery.Discover(Log);
				_selectedIndex = _targets.Length > 0 ? 0 : -1;
				_discovered = _targets.Length > 0;
				_status = _targets.Length > 0 ? $"Found {_targets.Length} local project(s)" : "No complete local payload found";
			}
			catch (Exception e)
			{
				_discovered = false;
				_targets = Array.Empty<SyncBuildInfo>();
				_selectedIndex = -1;
				_status = "Scan failed";
				Log("Discovery error: " + e.Message);
			}

			Repaint();
			return Task.CompletedTask;
		}

		private async Task ImportSelectedTarget()
		{
			if (_busy || !_discovered || _selectedIndex < 0 || _targets.Length == 0) return;
			var info = _targets[_selectedIndex];

			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			try
			{
				BeginBusy($"Import: {info.path}");

				var orchestrator = new ImportOrchestrator(null, _cache, Log, SetProgress);
				await orchestrator.Run(info, token);

				// Store sync paths so we can operate without reconnect later
				_cache.SaveSyncInfo(info);

				// Refresh linked marker state
				RefreshLinkedStateFromMarker();
				_linkedSyncInfo = _cache.LoadSyncInfo(info.path);

				_status = "Imported";
				Log("Import complete. Project is now linked.");
				ScaffoldInboxCheck();
			}
			catch (OperationCanceledException)
			{
				Log("Import cancelled.");
			}
			catch (Exception e)
			{
				Log("Import failed: " + e);
			}
			finally
			{
				EndBusy();
				Repaint();
			}
		}

		private async Task SyncLinkedGame()
		{
			if (_busy) return;
			if (string.IsNullOrWhiteSpace(_linkedGameId))
			{
				Log("No linked game detected (no marker).");
				return;
			}

			if (!TryResolveLatestLinkedSyncInfo(out var latest))
			{
				Log("Linked game files were not found on disk.");
				return;
			}

			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			try
			{
				BeginBusy($"Sync: {_linkedGameId}");

				var orchestrator = new ImportOrchestrator(null, _cache, Log, SetProgress);
				await orchestrator.Run(latest, token);

				// Update cached sync info too
				_cache.SaveSyncInfo(latest);
				_linkedSyncInfo = latest;

				_status = "Synced";
				Log("Sync complete.");
				ScaffoldInboxCheck();
			}
			catch (OperationCanceledException)
			{
				Log("Sync cancelled.");
			}
			catch (Exception e)
			{
				Log("Sync failed: " + e);
			}
			finally
			{
				EndBusy();
				Repaint();
			}
		}

		private async Task PublishLinkedGame()
		{
			if (_busy) return;

			if (string.IsNullOrWhiteSpace(_linkedGameId))
			{
				Log("No linked game detected (no marker).");
				return;
			}
			if (string.IsNullOrWhiteSpace(_cloudToken))
			{
				Log("Cloud token missing.");
				return;
			}

			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			try
			{
				BeginBusy($"Publish: {_linkedGameId}");

				// Determine revision for metadata (optional)
				var syncInfo = _linkedSyncInfo ?? _cache.LoadSyncInfo(_linkedGameId);
				var revision = ResolveRevisionFromSyncInfo(syncInfo) ?? _linkedRevision ?? "unknown";

				if (_autoSyncBeforePublish)
				{
					SetProgress("Refreshing local project files...", 0.06f);
					if (!TryResolveLatestLinkedSyncInfo(out var latest))
					{
						Log("Linked game files were not found on disk.");
						return;
					}

					SetProgress("Syncing...", 0.10f);
					var orchestrator = new ImportOrchestrator(null, _cache, Log, SetProgress);
					await orchestrator.Run(latest, token);

					_cache.SaveSyncInfo(latest);
					_linkedSyncInfo = latest;

					revision = ResolveRevisionFromSyncInfo(latest) ?? revision;
				}

				SetProgress("Building WebGL...", 0.35f);
				var publisher = new Publisher(Log, SetProgress);
				var zipPath = await publisher.BuildWebGLZip(_linkedGameId, revision, _devBuild, token);

				SetProgress("Requesting upload URL...", 0.70f);
				var fileBytes = new FileInfo(zipPath).Length;
				var buildHash = HashUtil.Sha256File(zipPath);

				var req = new PublishRequestUploadBody
				{
					gameId = _linkedGameId,
					revision = revision,
					unityVersion = Application.unityVersion,
					buildHash = buildHash,
					sizeBytes = fileBytes
				};

				var up = await _cloud.RequestUpload(req, token);

				SetProgress("Uploading zip...", 0.82f);
				await _cloud.PutFile(up.uploadUrl, zipPath, token);

				SetProgress("Committing publish...", 0.93f);
				var commit = await _cloud.Commit(new PublishCommitBody
				{
					artifactId = up.artifactId,
					gameId = _linkedGameId,
					revision = revision,
					buildHash = buildHash
				}, token);

				SetProgress("Done.", 1f);
				Log($"Publish complete. releaseId={commit.releaseId} status={commit.status} url={commit.url}");
			}
			catch (OperationCanceledException)
			{
				Log("Publish cancelled.");
			}
			catch (Exception e)
			{
				Log("Publish failed: " + e);
			}
			finally
			{
				EndBusy();
				Repaint();
			}
		}

		// -------------------------
		// Helpers
		// -------------------------

		private static string ResolveRevisionFromSyncInfo(SyncBuildInfo info)
		{
			try
			{
				if (info == null) return null;
				if (string.IsNullOrWhiteSpace(info.buildFilePath)) return null;
				if (!File.Exists(info.buildFilePath)) return null;

				var bj = PathJsonLoader.LoadJsonFile<BuildJson>(info.buildFilePath);
				return string.IsNullOrWhiteSpace(bj?.revision) ? null : bj.revision;
			}
			catch
			{
				return null;
			}
		}

		private void BeginBusy(string step)
		{
			_busy = true;
			_progress = 0;
			_step = step;
			_status = "Busy";
			Log("== " + step + " ==");
		}

		private void EndBusy()
		{
			_busy = false;
			_progress = 0;
			_step = "";
			_status = "Ready";
			_cts?.Dispose();
			_cts = null;
		}

		private void SetProgress(string step, float p)
		{
			_step = step;
			_progress = Mathf.Clamp01(p);
			Repaint();
		}

		private void Log(string msg)
		{
			_log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
			if (_log.Length > 200_000) _log.Remove(0, 50_000);
			Repaint();
		}

		private bool TryResolveLatestLinkedSyncInfo(out SyncBuildInfo info)
		{
			info = null;

			if (_linkedSyncInfo != null && LocalSyncDiscovery.TryFindByRoot(_linkedSyncInfo.path, out info))
				return true;

			if (!string.IsNullOrWhiteSpace(_linkedGameId) && LocalSyncDiscovery.TryFindByRoot(_linkedGameId, out info))
				return true;

			var discovered = LocalSyncDiscovery.Discover(Log);
			info = discovered.FirstOrDefault(t => string.Equals(t.path, _linkedGameId, StringComparison.OrdinalIgnoreCase));
			return info != null;
		}

		private static SyncBuildInfo BuildSyncInfoFromMarker(Plyground.Sync.Runtime.SceneMarker marker)
		{
			if (marker == null || string.IsNullOrWhiteSpace(marker.gameId))
				return null;

			return new SyncBuildInfo
			{
				path = marker.syncRootPath ?? marker.gameId,
				environmentPath = marker.environmentPath,
				gameItemPath = marker.gameItemPath,
				buildFilePath = marker.buildFilePath,
				modulePath = marker.modulePath,
				assetPath = marker.assetPath
			};
		}

		private void ScaffoldInboxCheck()
		{
			var inboxPath = LocalSyncDiscovery.GetInboxFolderAbsolutePath();
			try
			{
				Directory.CreateDirectory(inboxPath);
				Log($"Inbox scaffold ready at {inboxPath}");
				Log("Inbox change processing is not implemented yet.");
			}
			catch (Exception e)
			{
				Log("Inbox scaffold setup failed: " + e.Message);
			}
		}
	}
}
#endif
