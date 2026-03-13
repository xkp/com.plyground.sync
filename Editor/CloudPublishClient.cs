#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Plysync.Editor
{
	[Serializable]
	public class PublishRequestUploadBody
	{
		public string gameId;
		public string revision;
		public string unityVersion;
		public string buildHash;
		public long sizeBytes;
	}

	[Serializable]
	public class PublishRequestUploadResponse
	{
		public string artifactId;
		public string uploadUrl;
	}

	[Serializable]
	public class PublishCommitBody
	{
		public string artifactId;
		public string gameId;
		public string revision;
		public string buildHash;
	}

	[Serializable]
	public class PublishCommitResponse
	{
		public string releaseId;
		public string status;
		public string url;
	}

	public sealed class CloudPublishClient
	{
		private readonly string _baseUrl;
		private readonly Func<string> _getToken;
		private readonly Action<string> _log;

		public CloudPublishClient(string baseUrl, Func<string> getToken, Action<string> log)
		{
			_baseUrl = (baseUrl ?? "").TrimEnd('/');
			_getToken = getToken ?? (() => "");
			_log = log ?? (_ => { });
		}

		public Task<PublishRequestUploadResponse> RequestUpload(PublishRequestUploadBody body, CancellationToken ct)
			=> PostJson<PublishRequestUploadResponse>($"{_baseUrl}/api/publish/webgl/request-upload", body, ct);

		public Task<PublishCommitResponse> Commit(PublishCommitBody body, CancellationToken ct)
			=> PostJson<PublishCommitResponse>($"{_baseUrl}/api/publish/webgl/commit", body, ct);

		public async Task PutFile(string uploadUrl, string filePath, CancellationToken ct)
		{
			_log($"PUT {uploadUrl} (file={filePath})");
			var bytes = System.IO.File.ReadAllBytes(filePath);

			using (var req = new UnityWebRequest(uploadUrl, UnityWebRequest.kHttpVerbPUT))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/zip");

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
					throw new Exception($"PUT failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");
			}
		}

		private async Task<T> PostJson<T>(string url, object body, CancellationToken ct)
		{
			var json = UnityEngine.JsonUtility.ToJson(body);
			var bytes = Encoding.UTF8.GetBytes(json);

			_log($"POST {url}");
			using (var req = new UnityWebRequest(url, "POST"))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");

				var token = _getToken()?.Trim();
				if (!string.IsNullOrEmpty(token))
					req.SetRequestHeader("Authorization", "Bearer " + token);

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

				return UnityEngine.JsonUtility.FromJson<T>(req.downloadHandler.text);
			}
		}
	}
}
#endif