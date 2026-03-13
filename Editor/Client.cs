#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Plysync.Editor
{
	public sealed class PlysyncClient
	{
		private readonly string _baseUrl;
		private readonly Action<string> _log;

		public string BaseUrl => _baseUrl;

		public PlysyncClient(string baseUrl, Action<string> log)
		{
			_baseUrl = (baseUrl ?? "").TrimEnd('/');
			_log = log ?? (_ => { });
		}

		public Task<SyncBuildInfo[]> ListBuildInfos()
			=> GetJson<SyncBuildInfo[]>($"{_baseUrl}/sync/list", CancellationToken.None);

		public async Task<PackagesBlock> ResolvePackagesForModules(string[] moduleIds, CancellationToken ct)
		{
			if (moduleIds == null || moduleIds.Length == 0)
				return null;

			var body = new ModulePackagesResolveRequest { moduleIds = moduleIds };
			var endpoints = new[]
			{
				$"{_baseUrl}/sync/packages"
			};

			Exception lastError = null;

			for (int i = 0; i < endpoints.Length; i++)
			{
				var url = endpoints[i];
				try
				{
					var raw = await PostJsonRaw(url, body, ct);

					// Support either direct PackagesBlock or an envelope with "packages".
					var direct = JsonUtilitySafe.FromJson<PackagesBlock>($"{{ \"value\": {raw} }}");
					if (direct != null && HasAnyPackages(direct))
						return direct;

					//var envelope = JsonUtilitySafe.FromJson<ModulePackagesResolveResponse>(raw);
					//if (envelope != null)
					//{
					//	if (envelope.packages != null && HasAnyPackages(envelope.packages))
					//		return envelope.packages;

					//	var normalized = new PackagesBlock
					//	{
					//		upm = envelope.upm,
					//		unityPackages = envelope.unityPackages,
					//		assetStore = envelope.assetStore
					//	};
					//	if (HasAnyPackages(normalized))
					//		return normalized;
					//}

					_log("Package resolve returned no installable packages.");
					return null;
				}
				catch (Exception ex)
				{
					lastError = ex;
					var isLast = i == endpoints.Length - 1;
					if (!isLast)
						_log($"Package resolve failed at {url}: {ex.Message}. Trying fallback endpoint...");
				}
			}

			throw lastError ?? new Exception("Package resolve failed.");
		}

		private async Task<T> GetJson<T>(string url, CancellationToken ct)
		{
			_log($"GET {url}");
			using (var req = UnityWebRequest.Get(url))
			{
				req.downloadHandler = new DownloadHandlerBuffer();
				var op = req.SendWebRequest();

				while (!op.isDone)
				{
					if (ct.IsCancellationRequested)
					{
						req.Abort();
						ct.ThrowIfCancellationRequested();
					}
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
					throw new Exception($"GET failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

				return JsonUtilitySafe.FromJson<T>(req.downloadHandler.text);
			}
		}

		private async Task<string> PostJsonRaw(string url, object body, CancellationToken ct)
		{
			_log($"POST {url}");
			var json = UnityEngine.JsonUtility.ToJson(body ?? new object());
			var bytes = Encoding.UTF8.GetBytes(json);

			using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");

				var op = req.SendWebRequest();
				while (!op.isDone)
				{
					if (ct.IsCancellationRequested)
					{
						req.Abort();
						ct.ThrowIfCancellationRequested();
					}
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
					throw new Exception($"POST failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

				return req.downloadHandler?.text ?? "";
			}
		}

		private static bool HasAnyPackages(PackagesBlock pkgs)
		{
			if (pkgs == null) return false;
			return pkgs.value != null && pkgs.value.Length > 0;
		}
	}

	internal static class JsonUtilitySafe
	{
		[Serializable] private class Wrapper<T> { public T value; }

		public static T FromJson<T>(string json)
		{
			json = json?.Trim() ?? "";
			if (json.StartsWith("["))
			{
				var wrapped = "{\"value\":" + json + "}";
				return UnityEngine.JsonUtility.FromJson<Wrapper<T>>(wrapped).value;
			}
			return UnityEngine.JsonUtility.FromJson<T>(json);
		}
	}
}
#endif
