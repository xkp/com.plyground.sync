#if UNITY_EDITOR
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net;
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
		public string codeVerifier;
	}

	[Serializable]
	public class BrowserAuthExchangeResponse
	{
		public string accessToken;
		public string token;
	}

	public sealed class BrowserAuthSession
	{
		private const string AuthorizeUrlTemplate = "https://auth.plyground.ai/oauth2/auth?client_id=ffc836d2eebb4ccd9b6319a133f21035&response_type=code&scope=openid%20profile%20email&redirect_uri={0}";
		private const int CallbackPort = 42137;
		private const int ExchangeTimeoutMs = 30000;

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

			var prefix = $"http://127.0.0.1:{CallbackPort}/";
			var redirectUri = prefix + "callback/";
			var state = CreateState();
			var codeVerifier = CreateCodeVerifier();
			var codeChallenge = CreateCodeChallenge(codeVerifier);
			var loginUrl =
				string.Format(AuthorizeUrlTemplate, UnityWebRequest.EscapeURL(redirectUri)) +
				"&state=" + UnityWebRequest.EscapeURL(state) +
				"&code_challenge=" + UnityWebRequest.EscapeURL(codeChallenge) +
				"&code_challenge_method=S256";
			_log($"Auth listener preparing on {prefix}");
			_log($"Auth redirect URI: {redirectUri}");
			_log($"Auth state length: {state.Length}");
			_log($"PKCE verifier length: {codeVerifier.Length}");

			var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
			try
			{
				try
				{
					listener.Start();
				}
				catch (Exception ex)
				{
					_log("Failed to start local auth listener: " + ex);
					throw;
				}
				_log("Local auth listener started successfully.");

				_log($"Opening browser login at: {loginUrl}");
				Application.OpenURL(loginUrl);
				_log("Waiting for browser callback...");

				while (true)
				{
					ct.ThrowIfCancellationRequested();
					var callback = await GetContext(listener, prefix, ct);
					var requestUrl = callback.Url;
					_log("Received browser callback connection.");
					if (requestUrl == null)
					{
						_log("Browser callback URL was null.");
						await WriteHtml(callback, "Login failed", "The login callback URL was missing.");
						continue;
					}

					_log($"Browser callback URL: {requestUrl}");

					if (!string.Equals(requestUrl.AbsolutePath, "/callback/", StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(requestUrl.AbsolutePath, "/callback", StringComparison.OrdinalIgnoreCase))
					{
						_log("Ignoring callback on unexpected path: " + requestUrl.AbsolutePath);
						await WriteHtml(callback, "Plyground Login", "Waiting for the Plyground login callback...");
						continue;
					}

					var error = callback.Query["error"];
					if (!string.IsNullOrWhiteSpace(error))
					{
						var description = callback.Query["error_description"] ?? error;
						_log("Browser login returned error: " + description);
						await WriteHtml(callback, "Login failed", description);
						throw new Exception("Browser login failed: " + description);
					}

					var returnedState = callback.Query["state"];
					if (string.IsNullOrWhiteSpace(returnedState) || !string.Equals(returnedState, state, StringComparison.Ordinal))
					{
						_log($"Browser callback state mismatch. Expected='{state}' Returned='{returnedState}'");
						await WriteHtml(callback, "Login failed", "The login callback state did not match the original request.");
						throw new Exception("Browser login failed: callback state mismatch.");
					}
					_log("Browser callback state validated.");

					var token = FirstNonEmpty(
						callback.Query["access_token"],
						callback.Query["token"],
						callback.Query["id_token"]);

					if (!string.IsNullOrWhiteSpace(token))
					{
						_log($"Browser callback included token directly. Token length={token.Trim().Length}");
						await WriteHtml(callback, "Login complete", "You can close this browser window and return to Unity.");
						return token.Trim();
					}

					var code = callback.Query["code"];
					if (!string.IsNullOrWhiteSpace(code))
					{
						_log($"Browser callback included authorization code. Code length={code.Trim().Length}");
						await WriteHtml(callback, "Login received", "Plyground received your sign-in. You can close this browser window and return to Unity while we finish connecting your account.");
						_log("Browser response sent. Starting auth code exchange...");
						var exchangedToken = await ExchangeCodeForToken(code, redirectUri, codeVerifier, ct);
						_log($"Auth code exchange completed. Token length={exchangedToken?.Length ?? 0}");
						return exchangedToken;
					}

					_log("Browser callback did not include a token or code.");
					await WriteHtml(callback, "Login failed", "No token or code was returned to Unity.");
					throw new Exception("Browser login failed: callback did not include a token or code.");
				}
			}
			finally
			{
				try
				{
					listener.Stop();
				}
				catch
				{
				}
			}
		}

		private async Task<string> ExchangeCodeForToken(string code, string redirectUri, string codeVerifier, CancellationToken ct)
		{
			var url = $"{_baseUrl}/api/auth/unity/exchange";
			var body = new BrowserAuthExchangeRequest
			{
				code = code,
				redirectUri = redirectUri,
				codeVerifier = codeVerifier
			};

			var json = JsonUtility.ToJson(body);
			var bytes = Encoding.UTF8.GetBytes(json);

			_log($"Exchanging auth code at: {url}");
			_log($"Exchange payload redirectUri={redirectUri}");
			_log($"Exchange payload code length={code?.Length ?? 0}");
			_log($"Exchange payload codeVerifier length={codeVerifier?.Length ?? 0}");

			using (var req = new UnityWebRequest(url, "POST"))
			{
				req.uploadHandler = new UploadHandlerRaw(bytes);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");

				var op = req.SendWebRequest();
				var startedAt = DateTime.UtcNow;
				var lastLogSeconds = -1;
				while (!op.isDone)
				{
					ct.ThrowIfCancellationRequested();
					var elapsedSeconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
					if (elapsedSeconds != lastLogSeconds && elapsedSeconds > 0 && elapsedSeconds % 5 == 0)
					{
						lastLogSeconds = elapsedSeconds;
						_log($"Auth code exchange still waiting after {elapsedSeconds}s...");
					}
					if ((DateTime.UtcNow - startedAt).TotalMilliseconds > ExchangeTimeoutMs)
					{
						req.Abort();
						throw new Exception($"Auth code exchange timed out after {ExchangeTimeoutMs / 1000}s. Verify the backend '/api/auth/unity/exchange' endpoint is reachable and handles PKCE.");
					}
					await Task.Delay(50, ct);
				}

				if (req.result != UnityWebRequest.Result.Success)
				{
					_log($"Auth code exchange failed. Status={req.responseCode} Error={req.error} Body={req.downloadHandler?.text}");
					throw new Exception($"Auth code exchange failed: {req.responseCode} {req.error} body={req.downloadHandler?.text}");
				}

				_log($"Auth code exchange HTTP {req.responseCode}. Body={req.downloadHandler?.text}");
				var response = JsonUtility.FromJson<BrowserAuthExchangeResponse>(req.downloadHandler.text ?? "");
				var token = FirstNonEmpty(response?.accessToken, response?.token);
				if (string.IsNullOrWhiteSpace(token))
					throw new Exception("Auth code exchange succeeded but no access token was returned.");

				return token.Trim();
			}
		}

		private sealed class BrowserCallback
		{
			public TcpClient Client;
			public Uri Url;
			public System.Collections.Specialized.NameValueCollection Query;
		}

		private static async Task<BrowserCallback> GetContext(TcpListener listener, string prefix, CancellationToken ct)
		{
			var startedAt = DateTime.UtcNow;
			var lastLogSeconds = -1;
			var acceptTask = listener.AcceptTcpClientAsync();
			while (true)
			{
				ct.ThrowIfCancellationRequested();

				var completed = await Task.WhenAny(acceptTask, Task.Delay(100, ct));
				if (completed == acceptTask)
				{
					var client = await acceptTask;
					try
					{
						var stream = client.GetStream();
						var requestLine = await ReadRequestLine(stream, ct);
						if (string.IsNullOrWhiteSpace(requestLine))
							return new BrowserCallback { Client = client };

						var parts = requestLine.Split(' ');
						if (parts.Length < 2)
							return new BrowserCallback { Client = client };

						await DrainHeaders(stream, ct);

						var rawPath = parts[1];
						var fullUrl = rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
							? rawPath
							: prefix.TrimEnd('/') + rawPath;
						var url = new Uri(fullUrl);
						return new BrowserCallback
						{
							Client = client,
							Url = url,
							Query = ParseQuery(url.Query)
						};
					}
					catch
					{
						client.Close();
						throw;
					}
				}

				var elapsedSeconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
				if (elapsedSeconds != lastLogSeconds && elapsedSeconds > 0 && elapsedSeconds % 5 == 0)
				{
					lastLogSeconds = elapsedSeconds;
					Debug.Log($"[Plysync] Still waiting for auth callback on localhost after {elapsedSeconds}s...");
				}
			}
		}

		private static async Task WriteHtml(BrowserCallback callback, string title, string message)
		{
			if (callback == null || callback.Client == null)
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
			var header =
				"HTTP/1.1 200 OK\r\n" +
				"Content-Type: text/html; charset=utf-8\r\n" +
				"Content-Length: " + bytes.Length + "\r\n" +
				"Connection: close\r\n\r\n";
			var headerBytes = Encoding.ASCII.GetBytes(header);

			try
			{
				using (var output = callback.Client.GetStream())
				{
					await output.WriteAsync(headerBytes, 0, headerBytes.Length);
					await output.WriteAsync(bytes, 0, bytes.Length);
					await output.FlushAsync();
				}
			}
			finally
			{
				callback.Client.Close();
			}
		}

		private static async Task<string> ReadRequestLine(NetworkStream stream, CancellationToken ct)
		{
			var builder = new StringBuilder();
			var buffer = new byte[1];
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				var read = await stream.ReadAsync(buffer, 0, 1, ct);
				if (read <= 0)
					break;

				var ch = (char)buffer[0];
				if (ch == '\n')
					break;
				if (ch != '\r')
					builder.Append(ch);
			}

			return builder.ToString();
		}

		private static async Task DrainHeaders(NetworkStream stream, CancellationToken ct)
		{
			var lineCount = 0;
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				var line = await ReadRequestLine(stream, ct);
				if (string.IsNullOrEmpty(line))
					return;

				lineCount++;
				if (lineCount > 100)
					return;
			}
		}

		private static System.Collections.Specialized.NameValueCollection ParseQuery(string query)
		{
			var values = new System.Collections.Specialized.NameValueCollection();
			if (string.IsNullOrWhiteSpace(query))
				return values;

			var trimmed = query.StartsWith("?") ? query.Substring(1) : query;
			var parts = trimmed.Split('&');
			for (var i = 0; i < parts.Length; i++)
			{
				var part = parts[i];
				if (string.IsNullOrWhiteSpace(part))
					continue;

				var separatorIndex = part.IndexOf('=');
				var rawKey = separatorIndex >= 0 ? part.Substring(0, separatorIndex) : part;
				var rawValue = separatorIndex >= 0 ? part.Substring(separatorIndex + 1) : "";
				var key = Uri.UnescapeDataString(rawKey.Replace("+", "%20"));
				var value = Uri.UnescapeDataString(rawValue.Replace("+", "%20"));
				values[key] = value;
			}

			return values;
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

		private static string CreateState()
		{
			var bytes = new byte[24];
			using (var rng = RandomNumberGenerator.Create())
			{
				rng.GetBytes(bytes);
			}

			return Convert.ToBase64String(bytes)
				.TrimEnd('=')
				.Replace('+', '-')
				.Replace('/', '_');
		}

		private static string CreateCodeVerifier()
		{
			var bytes = new byte[32];
			using (var rng = RandomNumberGenerator.Create())
			{
				rng.GetBytes(bytes);
			}

			return Convert.ToBase64String(bytes)
				.TrimEnd('=')
				.Replace('+', '-')
				.Replace('/', '_');
		}

		private static string CreateCodeChallenge(string codeVerifier)
		{
			var bytes = Encoding.ASCII.GetBytes(codeVerifier ?? "");
			using (var sha = SHA256.Create())
			{
				var hash = sha.ComputeHash(bytes);
				return Convert.ToBase64String(hash)
					.TrimEnd('=')
					.Replace('+', '-')
					.Replace('/', '_');
			}
		}
	}
}
#endif
