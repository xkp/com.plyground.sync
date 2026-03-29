#if UNITY_EDITOR
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Plysync.Editor
{
	[Serializable]
	public class BrowserAuthExchangeRequest
	{
		public string code;
		public string redirectUri;
	}

	[Serializable]
	public class BrowserAuthExchangeResponse
	{
		public string accessToken;
		public string token;
	}

	public sealed class BrowserAuthSession
	{
		private readonly string _baseUrl;
		private readonly Action<string> _log;

		public BrowserAuthSession(string baseUrl, Action<string> log)
		{
			_baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
			_log = log ?? (_ => { });
		}

		public async Task<string> Login(CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(_baseUrl))
				throw new Exception("Cloud API base URL is not configured.");

			var port = ReserveLocalPort();
			var prefix = $"http://127.0.0.1:{port}/";
			var redirectUri = prefix + "callback/";
			var loginUrl = $"{_baseUrl}/api/auth/unity/start?redirect_uri={UnityWebRequest.EscapeURL(redirectUri)}";

			using (var listener = new HttpListener())
			{
				listener.Prefixes.Add(prefix);
				listener.Start();

				_log($"Opening browser login at: {loginUrl}");
				Application.OpenURL(loginUrl);

				while (true)
				{
					ct.ThrowIfCancellationRequested();
					var context = await GetContext(listener, ct);
					var request = context.Request;
					if (request == null)
					{
						await WriteHtml(context.Response, "Login failed", "The login callback did not include a request.");
						continue;
					}

					if (request.Url == null)
					{
						await WriteHtml(context.Response, "Login failed", "The login callback URL was missing.");
						continue;
					}

					if (!string.Equals(request.Url.AbsolutePath, "/callback/", StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(request.Url.AbsolutePath, "/callback", StringComparison.OrdinalIgnoreCase))
					{
						await WriteHtml(context.Response, "Plyground Login", "Waiting for the Plyground login callback...");
						continue;
					}

					var error = request.QueryString["error"];
					if (!string.IsNullOrWhiteSpace(error))
					{
						var description = request.QueryString["error_description"] ?? error;
						await WriteHtml(context.Response, "Login failed", description);
						throw new Exception("Browser login failed: " + description);
					}

					var token = FirstNonEmpty(
						request.QueryString["access_token"],
						request.QueryString["token"],
						request.QueryString["id_token"]);

					if (!string.IsNullOrWhiteSpace(token))
					{
						await WriteHtml(context.Response, "Login complete", "You can close this browser window and return to Unity.");
						return token.Trim();
					}

					var code = request.QueryString["code"];
					if (!string.IsNullOrWhiteSpace(code))
					{
						var exchangedToken = await ExchangeCodeForToken(code, redirectUri, ct);
						await WriteHtml(context.Response, "Login complete", "You can close this browser window and return to Unity.");
						return exchangedToken;
					}

					await WriteHtml(context.Response, "Login failed", "No token or code was returned to Unity.");
					throw new Exception("Browser login failed: callback did not include a token or code.");
				}
			}
		}

		private async Task<string> ExchangeCodeForToken(string code, string redirectUri, CancellationToken ct)
		{
			var url = $"{_baseUrl}/api/auth/unity/exchange";
			var body = new BrowserAuthExchangeRequest
			{
				code = code,
				redirectUri = redirectUri
			};

			var json = JsonUtility.ToJson(body);
			var bytes = Encoding.UTF8.GetBytes(json);

			_log($"Exchanging auth code at: {url}");

			using (var req = new UnityWebRequest(url, "POST"))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");

				var op = req.SendWebRequest();
				while (!op.isDone)
				{
					ct.ThrowIfCancellationRequested();
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
					throw new Exception($"Auth code exchange failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");

				var response = JsonUtility.FromJson<BrowserAuthExchangeResponse>(req.downloadHandler.text ?? "");
				var token = FirstNonEmpty(response?.accessToken, response?.token);
				if (string.IsNullOrWhiteSpace(token))
					throw new Exception("Auth code exchange succeeded but no access token was returned.");

				return token.Trim();
			}
		}

		private static async Task<HttpListenerContext> GetContext(HttpListener listener, CancellationToken ct)
		{
			while (true)
			{
				ct.ThrowIfCancellationRequested();

				var contextTask = listener.GetContextAsync();
				var completed = await Task.WhenAny(contextTask, Task.Delay(100, ct));
				if (completed == contextTask)
					return await contextTask;
			}
		}

		private static async Task WriteHtml(HttpListenerResponse response, string title, string message)
		{
			if (response == null)
				return;

			var safeTitle = WebUtility.HtmlEncode(title ?? "Plyground");
			var safeMessage = WebUtility.HtmlEncode(message ?? "");
			var html = $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"" />
  <title>{safeTitle}</title>
  <style>
    body {{ font-family: Arial, sans-serif; background: #f4f1e8; color: #1f2a1f; padding: 32px; }}
    .card {{ max-width: 560px; margin: 48px auto; background: white; border-radius: 12px; padding: 28px; box-shadow: 0 12px 28px rgba(0,0,0,0.08); }}
    h1 {{ margin-top: 0; }}
  </style>
</head>
<body>
  <div class=""card"">
    <h1>{safeTitle}</h1>
    <p>{safeMessage}</p>
  </div>
</body>
</html>";

			var bytes = Encoding.UTF8.GetBytes(html);
			response.StatusCode = 200;
			response.ContentType = "text/html; charset=utf-8";
			response.ContentLength64 = bytes.Length;

			using (var output = response.OutputStream)
			{
				await output.WriteAsync(bytes, 0, bytes.Length);
			}
		}

		private static int ReserveLocalPort()
		{
			using (var tcp = new TcpListener(IPAddress.Loopback, 0))
			{
				tcp.Start();
				return ((IPEndPoint)tcp.LocalEndpoint).Port;
			}
		}

		private static string FirstNonEmpty(params string[] values)
		{
			if (values == null)
				return null;

			for (var i = 0; i < values.Length; i++)
			{
				var value = values[i];
				if (!string.IsNullOrWhiteSpace(value))
					return value;
			}

			return null;
		}
	}
}
#endif
