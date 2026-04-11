#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Plysync.Editor
{
	[Serializable]
	public sealed class LocalPublishRequest
	{
		public string variationId;
	}

	[Serializable]
	public sealed class LocalPublishResponse
	{
		public bool success;
		public string url;
		public string liveUrl;
		public string message;
		public string error;
	}

	public sealed class LocalPublishClient
	{
		private readonly string _baseUrl;
		private readonly Action<string> _log;

		public LocalPublishClient(string baseUrl, Action<string> log)
		{
			_baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
			_log = log ?? (_ => { });
		}

		public async Task<LocalPublishResponse> Publish(string variationId, CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(_baseUrl))
				throw new Exception("Plyground local server URL is not configured.");
			if (string.IsNullOrWhiteSpace(variationId))
				throw new Exception("Variation ID is required for publish.");

			var url = _baseUrl + "/publish";
			var body = new LocalPublishRequest
			{
				variationId = variationId.Trim()
			};

			var json = UnityEngine.JsonUtility.ToJson(body);
			var bytes = Encoding.UTF8.GetBytes(json);

			_log($"POST {url}");

			try
			{
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
						throw new Exception($"Publish failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

					var responseText = req.downloadHandler?.text ?? "";
					_log("Local publish response: " + responseText);
					var response = UnityEngine.JsonUtility.FromJson<LocalPublishResponse>(responseText);
					if (response == null)
						throw new Exception("Local publish returned an empty response.");

					return response;
				}
			}
			catch (Exception ex) when (IsLocalServerUnavailable(ex))
			{
				throw new Exception("Could not reach the Plyground local server. The Plyground app must be running.", ex);
			}
		}

		private static bool IsLocalServerUnavailable(Exception ex)
		{
			var text = ex?.ToString() ?? "";
			return text.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("cannot connect", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("failed to connect", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
#endif
