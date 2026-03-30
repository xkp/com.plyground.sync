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
		private const string DefaultLocalPublishServerBaseUrl = "http://localhost:4300";
		private const string DefaultLogoAssetPath = "Assets/plyground/Editor/logo.png";
		private const string PackageLogoAssetPath = "Packages/ai.plyground.sync/Editor/logo.png";

		private enum UiState
		{
			NotPlygroundProject,
			FirstRunImport,
			LinkedProject
		}

		private string _localPublishServerBaseUrl;
		private string _lastPublishedGameUrl = "";

		private bool _showAdvanced;

		private string _status = "Starting...";
		private readonly StringBuilder _log = new StringBuilder();
		private Vector2 _scroll;
		private string _publishErrorMessage;

		private bool _busy;
		private float _progress;
		private string _step = "";
		private CancellationTokenSource _cts;

		private CacheStore _cache;

		private bool _discovered;
		private SyncBuildInfo[] _targets = Array.Empty<SyncBuildInfo>();
		private int _selectedIndex = -1;

		// Linked/offline (marker-driven)
		private SyncBuildInfo _linkedSyncInfo; // cached sync paths
		private string _linkedGameId;          // marker.gameId (we store SyncBuildInfo.path here)
		private string _linkedRevision;        // marker.revision (optional)

		private bool _autoSyncBeforePublish = true;
		private Texture2D _logoTexture;
		private GUIStyle _headerBodyStyle;
		private GUIStyle _gamePopupStyle;

		[MenuItem("Plyground/Sync")]
		public static void Open() => GetWindow<PlysyncWindow>("plyground");

		private void OnEnable()
		{
			_cache = new CacheStore();

			_localPublishServerBaseUrl = EditorPrefs.GetString("Plysync.LocalPublishServerBaseUrl", DefaultLocalPublishServerBaseUrl);
			_showAdvanced = EditorPrefs.GetBool("Plysync.ShowAdvanced", false);
			_logoTexture = LoadLogoTexture();
			if (_log.Length == 0)
				_log.Append(ImportSessionState.LoadLog());

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
			EditorPrefs.SetString("Plysync.LocalPublishServerBaseUrl", _localPublishServerBaseUrl);
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
				if (GUILayout.Button("Clear Logs", GUILayout.Width(110)))
				{
					_log.Clear();
					ImportSessionState.ClearLog();
				}
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

			var packageDirect = AssetDatabase.LoadAssetAtPath<Texture2D>(PackageLogoAssetPath);
			if (packageDirect != null) return packageDirect;

			var guids = AssetDatabase.FindAssets("logo t:Texture2D", new[] { "Assets/plyground/Editor" });
			if (guids != null && guids.Length > 0)
			{
				var path = AssetDatabase.GUIDToAssetPath(guids[0]);
				return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
			}

			guids = AssetDatabase.FindAssets("logo t:Texture2D", new[] { "Packages/ai.plyground.sync/Editor" });
			if (guids == null || guids.Length == 0) return null;

			var packagePath = AssetDatabase.GUIDToAssetPath(guids[0]);
			return AssetDatabase.LoadAssetAtPath<Texture2D>(packagePath);
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
					return "Current state: this is an imported Plyground project. What to do: sync from disk if needed, then publish through the local Plyground app when you're ready.";
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
			EditorGUILayout.HelpBox("Publish sends this project's variation ID to the local Plyground app. The Plyground app only needs to be running when you click Publish.", MessageType.Info);

			_autoSyncBeforePublish = EditorGUILayout.Toggle("Sync before publish", _autoSyncBeforePublish);

			if (string.IsNullOrWhiteSpace(_linkedSyncInfo?.variationId))
				EditorGUILayout.HelpBox("Variation ID will be resolved when you click Publish. If it cannot be found then, publish will show an error.", MessageType.Info);

			if (!string.IsNullOrWhiteSpace(_publishErrorMessage))
				EditorGUILayout.HelpBox(_publishErrorMessage, MessageType.Error);

			GUI.enabled = !_busy && !string.IsNullOrWhiteSpace(_linkedGameId);
			if (GUILayout.Button("Publish", GUILayout.Height(30)))
				_ = PublishLinkedGame();
			GUI.enabled = true;

			if (!string.IsNullOrWhiteSpace(_lastPublishedGameUrl))
			{
				GUI.enabled = !_busy;
				if (GUILayout.Button("Run Game", GUILayout.Height(26)))
					Application.OpenURL(_lastPublishedGameUrl);
				GUI.enabled = true;
			}

			EditorGUILayout.Space(8);
			DrawAdvancedFoldout();
		}

		private void DrawAdvancedFoldout()
		{
			_showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
			if (!_showAdvanced) return;

			EditorGUI.indentLevel++;
			_localPublishServerBaseUrl = EditorGUILayout.TextField("Plyground Local Server", _localPublishServerBaseUrl);

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("Reset defaults", GUILayout.Width(140)))
				{
					_localPublishServerBaseUrl = DefaultLocalPublishServerBaseUrl;
				}

				if (GUILayout.Button("Save", GUILayout.Width(100)))
				{
					EditorPrefs.SetString("Plysync.LocalPublishServerBaseUrl", _localPublishServerBaseUrl);
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
			if (string.IsNullOrWhiteSpace(_localPublishServerBaseUrl)) _localPublishServerBaseUrl = DefaultLocalPublishServerBaseUrl;
			_localPublishServerBaseUrl = (_localPublishServerBaseUrl ?? "").Trim().TrimEnd('/');

			if (ImportSessionState.TryLoadPendingImportPath(out var pendingImportPath))
			{
				if (TryResolvePendingImportInfo(pendingImportPath, out var pendingImport))
				{
					Log($"Resuming pending import after package install: {pendingImport.path}");
					ImportSessionState.ClearPendingImportPath();
					await RunImport(pendingImport, "Resume Import");
					return;
				}

				Log($"Pending import could not be resolved after package install: {pendingImportPath}");
				ImportSessionState.ClearPendingImportPath();
			}

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
			await RunImport(info, "Import");
		}

		private async Task RunImport(SyncBuildInfo info, string actionLabel)
		{
			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			try
			{
				BeginBusy($"{actionLabel}: {info.path}");

				var orchestrator = new ImportOrchestrator(null, _cache, Log, SetProgress);
				var result = await orchestrator.Run(info, token);
				if (result == ImportRunResult.DeferredForReload)
				{
					_status = "Waiting for reload";
					Log("Import paused so Unity can reload assemblies. The import will resume automatically.");
					return;
				}

				// Store sync paths so we can operate without reconnect later
				_cache.SaveSyncInfo(info);

				// Refresh linked marker state
				RefreshLinkedStateFromMarker();
				_linkedSyncInfo = _cache.LoadSyncInfo(info.path);

				_status = "Imported";
				Log("Import complete. Project is now linked.");
				ImportSessionState.ClearPendingImportPath();
				ScaffoldInboxCheck();
			}
			catch (OperationCanceledException)
			{
				Log("Import cancelled.");
				ImportSessionState.ClearPendingImportPath();
			}
			catch (Exception e)
			{
				Log("Import failed: " + e);
				ImportSessionState.ClearPendingImportPath();
			}
			finally
			{
				EndBusy();
				Repaint();
			}
		}

		private Task SyncLinkedGame()
		{
			if (_busy) return Task.CompletedTask;
			if (string.IsNullOrWhiteSpace(_linkedGameId))
			{
				Log("No linked game detected (no marker).");
				return Task.CompletedTask;
			}

			if (!TryResolveLatestLinkedSyncInfo(out var latest))
			{
				Log("Linked game files were not found on disk.");
				return Task.CompletedTask;
			}

			_linkedSyncInfo = latest;
			_cache.SaveSyncInfo(latest);
			_status = "Sync not implemented";
			Log("Sync is not implemented yet. Future sync will match the current scene and apply changes instead of re-importing.");
			Repaint();
			return Task.CompletedTask;
		}

		private async Task PublishLinkedGame()
		{
			if (_busy) return;

			if (string.IsNullOrWhiteSpace(_linkedGameId))
			{
				Log("No linked game detected (no marker).");
				return;
			}
			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			try
			{
				BeginBusy($"Publish: {_linkedGameId}");
				_publishErrorMessage = null;

				// Determine revision for metadata (optional)
				var syncInfo = _linkedSyncInfo ?? _cache.LoadSyncInfo(_linkedGameId);
				EnsureVariationId(syncInfo);
				EnsureVariationId(_linkedSyncInfo);
				var revision = ResolveRevisionFromSyncInfo(syncInfo) ?? _linkedRevision ?? "unknown";

				if (_autoSyncBeforePublish)
				{
					SetProgress("Refreshing local project files...", 0.06f);
					if (!TryResolveLatestLinkedSyncInfo(out var latest))
						throw new Exception("Linked game files were not found on disk.");

					_cache.SaveSyncInfo(latest);
					_linkedSyncInfo = latest;
					EnsureVariationId(_linkedSyncInfo);
					Log("Sync before publish is not implemented yet. Publish will continue without applying scene changes.");

					revision = ResolveRevisionFromSyncInfo(latest) ?? revision;
				}

				var variationId = ResolveVariationId(_linkedSyncInfo) ?? ResolveVariationId(syncInfo);
				if (string.IsNullOrWhiteSpace(variationId))
					throw new Exception("Variation ID was not found for this project.");

				SetProgress("Building WebGL...", 0.12f);
				var publisher = new Publisher(Log, SetProgress);
				var buildPath = await publisher.BuildWebGL(variationId, revision, developmentBuild: false, token);
				Log($"WebGL build ready: {buildPath}");

				SetProgress("Publishing via Plyground app...", 0.85f);
				_lastPublishedGameUrl = "";
				var localPublish = new LocalPublishClient(_localPublishServerBaseUrl, Log);
				var response = await localPublish.Publish(variationId, token);
				var publishedUrl = !string.IsNullOrWhiteSpace(response.gameUrl) ? response.gameUrl : response.url;
				if (!response.success && string.IsNullOrWhiteSpace(publishedUrl))
				{
					var errorText = !string.IsNullOrWhiteSpace(response.error) ? response.error : response.message;
					throw new Exception(string.IsNullOrWhiteSpace(errorText) ? "Local publish reported failure." : errorText);
				}

				_lastPublishedGameUrl = publishedUrl;
				_publishErrorMessage = null;
				SetProgress("Done.", 1f);
				Log($"Publish complete. url={_lastPublishedGameUrl}");
			}
			catch (OperationCanceledException)
			{
				Log("Publish cancelled.");
			}
			catch (Exception e)
			{
				Log("Publish failed: " + e);
				NotifyPublishFailure(e.Message);
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
			ImportSessionState.SaveLog(_log.ToString());
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

		private static SyncBuildInfo BuildSyncInfoFromMarker(SceneMarker marker)
		{
			if (marker == null || string.IsNullOrWhiteSpace(marker.gameId))
				return null;

			return new SyncBuildInfo
			{
				variationId = !string.IsNullOrWhiteSpace(marker.variationId)
					? marker.variationId
					: LocalSyncDiscovery.GetVariationIdFromRoot(marker.syncRootPath ?? marker.gameId),
				path = marker.syncRootPath ?? marker.gameId,
				environmentPath = marker.environmentPath,
				gameItemPath = marker.gameItemPath,
				buildFilePath = marker.buildFilePath,
				modulePath = marker.modulePath,
				assetPath = marker.assetPath
			};
		}

		private bool TryResolvePendingImportInfo(string pendingImportPath, out SyncBuildInfo info)
		{
			info = null;
			if (string.IsNullOrWhiteSpace(pendingImportPath))
				return false;

			info = _cache.LoadSyncInfo(pendingImportPath);
			if (info != null)
				return true;

			if (LocalSyncDiscovery.TryFindByRoot(pendingImportPath, out info))
				return true;

			var discovered = LocalSyncDiscovery.Discover(Log);
			info = discovered.FirstOrDefault(t => string.Equals(t.path, pendingImportPath, StringComparison.OrdinalIgnoreCase));
			return info != null;
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

		private static string ResolveVariationId(SyncBuildInfo info)
		{
			if (info == null) return null;
			if (!string.IsNullOrWhiteSpace(info.variationId))
				return info.variationId;

			var derivedVariationId = LocalSyncDiscovery.GetVariationIdFromRoot(info.path);
			return string.IsNullOrWhiteSpace(derivedVariationId) ? null : derivedVariationId;
		}

		private static void EnsureVariationId(SyncBuildInfo info)
		{
			if (info == null || !string.IsNullOrWhiteSpace(info.variationId))
				return;

			info.variationId = ResolveVariationId(info);
		}

		private void NotifyPublishFailure(string message)
		{
			var resolvedMessage = BuildPublishFailureMessage(message);
			_publishErrorMessage = resolvedMessage;
			_status = "Publish failed";
			Repaint();
		}

		private static string BuildPublishFailureMessage(string message)
		{
			var text = message ?? "";
			if (text.IndexOf("Could not reach the Plyground local server", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("cannot connect", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("failed to connect", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return "The Plyground app should be running before you publish.";
			}

			return "An unexpected error happened while publishing.";
		}
	}
}
#endif
