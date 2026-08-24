using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Static HTTP server for local testing of remote content.
    /// Serves files from ServerData/ directory with correct Cache-Control headers and range request support.
    /// </summary>
    public class LocalContentServer
    {
        public class RequestEventArgs : EventArgs
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public int StatusCode { get; set; }
            public string CacheControl { get; set; }
            public long BytesSent { get; set; }
            public DateTime Timestamp { get; set; }
            /// <summary>
            /// True if the path matched the infra §3 cache policy matrix.
            /// False if the path fell through to the default policy (indicates a potential cache rule mismatch).
            /// </summary>
            public bool PathMatchesInfraPolicy { get; set; }
        }

        private HttpListener _listener;
        private int _currentPort;
        private bool _isRunning;
        private string _serverDataPath;

        public event EventHandler<RequestEventArgs> RequestReceived;
        public bool IsRunning => _isRunning;
        public int ActivePort => _currentPort;

        /// <summary>The port the server will use, whether or not it is running.</summary>
        /// <remarks>
        /// <see cref="ActivePort"/> is 0 until a server starts, so anything that names the port in
        /// text - a section subtitle, a hint - would print "localhost:0" while stopped. This reads the
        /// port the next start would use, which is what a reader wants to know before starting it.
        /// </remarks>
        public static int ConfiguredPort =>
            SessionState.GetInt(PortKey, 8080);
        public string ServerDataPath => _serverDataPath;

        public LocalContentServer()
        {
            _serverDataPath = Path.Combine(Directory.GetCurrentDirectory(), "ServerData");
            RegisterCleanupHooks();
        }

        private void RegisterCleanupHooks()
        {
            // Clean up before domain reload (recompile)
            // Shutdown, NOT Stop: a domain reload is not the user deciding to stop the server, and
            // Stop would clear the intent this reload is supposed to carry across.
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;

            // Clean up when editor quits
            // Also Shutdown: SessionState dies with the editor anyway, so clearing the flag here would
            // be redundant, and using Stop would make the two teardown paths differ for no reason.
            EditorApplication.quitting -= Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        public void Start(int port)
        {
            if (_isRunning)
            {
                Debug.LogWarning("LocalContentServer is already running");
                return;
            }

            // Record the intent BEFORE trying, so a domain reload during startup still restores it.
            // See LocalContentServerMenu's static constructor for why this is needed at all.
            SessionState.SetBool(WantsToRunKey, true);
            SessionState.SetInt(PortKey, port);

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                _currentPort = port;
                _isRunning = true;

                Debug.Log($"LocalContentServer started on http://localhost:{port}");
                Debug.Log($"Serving files from: {_serverDataPath}");

                _listener.BeginGetContext(OnRequestReceived, null);
            }
            catch (HttpListenerException ex)
            {
                // Win32 error codes on Windows (not BSD errno)
                if (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    Debug.LogError($"Failed to start LocalContentServer on port {port}: Access Denied (ErrorCode: {ex.ErrorCode})\n" +
                        $"This usually means the URL prefix requires admin privileges or a URL ACL reservation.\n" +
                        $"Try running the editor as administrator, or use: netsh http add urlacl url=http://localhost:{port}/ user=Everyone\n" +
                        $"Message: {ex.Message}");
                }
                else if (ex.ErrorCode == 183 || ex.ErrorCode == 32) // ERROR_ALREADY_EXISTS or ERROR_SHARING_VIOLATION
                {
                    Debug.LogError($"Failed to start LocalContentServer on port {port}: Port already in use (ErrorCode: {ex.ErrorCode})\n" +
                        $"Please use a different port or stop the application that is using port {port}.\n" +
                        $"Message: {ex.Message}");
                }
                else
                {
                    Debug.LogError($"Failed to start LocalContentServer on port {port}: ErrorCode {ex.ErrorCode}\n" +
                        $"Message: {ex.Message}");
                }
                _isRunning = false;
                _listener = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start LocalContentServer: {ex.Message}");
                _isRunning = false;
                _listener = null;
            }
        }

        /// <summary>SessionState keys backing the restart-after-domain-reload behaviour.</summary>
        /// <remarks>
        /// SessionState is the right lifetime here and EditorPrefs is not: it survives a domain reload
        /// but dies when the editor closes, which is exactly the lifetime of a running HttpListener.
        /// An EditorPrefs flag would outlive the thing it describes and would try to start a server on
        /// the next launch that nobody asked for.
        /// </remarks>
        internal const string WantsToRunKey = "AddressableManager.LocalContentServer.WantsToRun";
        internal const string PortKey = "AddressableManager.LocalContentServer.Port";

        /// <summary>
        /// Stop the server because the user asked. The server stays stopped across domain reloads.
        /// </summary>
        public void Stop()
        {
            // An explicit stop is an instruction, not an accident - do not resurrect it after the next
            // domain reload.
            SessionState.SetBool(WantsToRunKey, false);
            Shutdown();
        }

        /// <summary>
        /// Release the listener WITHOUT touching the run intent, for teardown the user did not ask for.
        /// </summary>
        /// <remarks>
        /// This split is load-bearing, and its absence silently disabled the restart-after-reload
        /// behaviour entirely in 4.1.0-pre.14.
        ///
        /// <c>AssemblyReloadEvents.beforeAssemblyReload</c> is wired to tear the listener down, because
        /// an HttpListener cannot survive the domain going away. When that was wired to the PUBLIC
        /// <see cref="Stop"/>, every domain reload cleared the "wants to run" flag microseconds before
        /// the reload that was supposed to read it - so the flag was never true on the other side and
        /// the server never came back. The feature added to fix "the local server dies on entering play
        /// mode" could not fire even once.
        ///
        /// An automatic teardown is not a decision about whether the server should be running. Only
        /// <see cref="Stop"/> is.
        /// </remarks>
        internal void Shutdown()
        {
            if (!_isRunning) return;

            _isRunning = false;

            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listener = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error stopping LocalContentServer: {ex.Message}");
            }

            Debug.Log("LocalContentServer stopped");
        }

        private void OnRequestReceived(IAsyncResult result)
        {
            if (!_isRunning) return;

            try
            {
                var context = _listener.EndGetContext(result);
                _listener.BeginGetContext(OnRequestReceived, null);

                HandleRequest(context);
            }
            catch (ObjectDisposedException)
            {
                // Server was stopped
            }
            catch (HttpListenerException)
            {
                // Listener was stopped or closed
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error handling request: {ex.Message}");
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var requestPath = request.Url.AbsolutePath;
                // Evaluate cache policy once for this request, reuse across all response paths
                var (cacheControl, matchesPolicy) = GetCacheControlHeader(requestPath);

                // Fault injection runs before anything else, so an injected 500 or a dropped
                // connection is indistinguishable to the client from the real thing. Tasks 5.1
                // and design doc §12 need failures that are reproducible on demand; waiting for
                // a real CDN to misbehave is not a test strategy.
                if (ServerFaults.TryApply(requestPath, context, out int injectedStatus))
                {
                    RaiseRequestEvent(request.HttpMethod, requestPath, injectedStatus, cacheControl, 0, matchesPolicy);
                    return;
                }

                var filePath = Path.Combine(_serverDataPath, requestPath.TrimStart('/'));

                // Security: prevent directory traversal
                var fullPath = Path.GetFullPath(filePath);
                var fullServerPath = Path.GetFullPath(_serverDataPath);
                if (!fullPath.StartsWith(fullServerPath))
                {
                    SendErrorResponse(context, 403, "Forbidden");
                    RaiseRequestEvent(request.HttpMethod, requestPath, 403, cacheControl, 0, matchesPolicy);
                    return;
                }

                if (!File.Exists(filePath))
                {
                    SendErrorResponse(context, 404, "Not Found");
                    RaiseRequestEvent(request.HttpMethod, requestPath, 404, cacheControl, 0, matchesPolicy);
                    return;
                }

                var contentType = GetContentType(filePath);
                var fileInfo = new FileInfo(filePath);
                var fileSize = fileInfo.Length;

                if (!matchesPolicy)
                {
                    Debug.LogWarning($"LocalContentServer: Path does not match infra §3 cache policy matrix: {requestPath}");
                }

                response.ContentType = contentType;
                response.AddHeader("Cache-Control", cacheControl);
                response.AddHeader("ETag", $"\"{fileInfo.LastWriteTimeUtc.Ticks:x}\"");
                response.AddHeader("Accept-Ranges", "bytes");

                // Handle range requests
                if (request.Headers.Get("Range") != null)
                {
                    HandleRangeRequest(context, filePath, fileSize, matchesPolicy);
                }
                else
                {
                    // Normal request
                    response.ContentLength64 = fileSize;
                    response.StatusCode = 200;

                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // Throttled when a bandwidth cap is set, so the reported download speed can
                        // be checked against a known rate (Phase 3 exit criterion: within ±15%).
                        // CopyTo would finish a local file instantly and measure nothing.
                        ServerFaults.CopyThrottled(fileStream, response.OutputStream);
                    }

                    RaiseRequestEvent(request.HttpMethod, requestPath, 200, cacheControl, fileSize, matchesPolicy);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing request: {ex.Message}");
                try
                {
                    var requestPath = context.Request.Url.AbsolutePath;
                    var (cacheControl, matchesPolicy) = GetCacheControlHeader(requestPath);
                    SendErrorResponse(context, 500, "Internal Server Error");
                    RaiseRequestEvent(context.Request.HttpMethod, requestPath, 500, cacheControl, 0, matchesPolicy);
                }
                catch { }
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private void HandleRangeRequest(HttpListenerContext context, string filePath, long fileSize, bool matchesPolicy)
        {
            var request = context.Request;
            var response = context.Response;

            var rangeHeader = request.Headers.Get("Range");
            if (!ParseRangeHeader(rangeHeader, fileSize, out var start, out var end))
            {
                SendErrorResponse(context, 416, "Range Not Satisfiable");
                return;
            }

            var length = end - start + 1;
            response.StatusCode = 206;
            response.ContentLength64 = length;
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileSize}");

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileStream.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[65536]; // 64KB buffer
                long remaining = length;

                while (remaining > 0)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, remaining);
                    int bytesRead = fileStream.Read(buffer, 0, bytesToRead);

                    if (bytesRead == 0) break;

                    response.OutputStream.Write(buffer, 0, bytesRead);
                    remaining -= bytesRead;
                }
            }

            var requestPath = request.Url.AbsolutePath;
            var cacheControl = response.Headers.Get("Cache-Control");
            RaiseRequestEvent(request.HttpMethod, requestPath, 206, cacheControl, length, matchesPolicy);
        }

        private bool ParseRangeHeader(string rangeHeader, long fileSize, out long start, out long end)
        {
            start = 0;
            end = 0;

            if (string.IsNullOrEmpty(rangeHeader))
                return false;

            // Parse "bytes=start-end" format
            var match = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
            if (!match.Success)
                return false;

            if (!long.TryParse(match.Groups[1].Value, out start))
                return false;

            if (string.IsNullOrEmpty(match.Groups[2].Value))
            {
                end = fileSize - 1;
            }
            else if (!long.TryParse(match.Groups[2].Value, out end))
            {
                return false;
            }

            // Validate range
            if (start > end || start < 0 || end >= fileSize)
                return false;

            return true;
        }

        /// <summary>
        /// Returns cache control header and a flag indicating if the path matched the infra §3 policy matrix.
        /// Unknown paths return no-store (default safe policy) and matchesPolicy=false to highlight potential cache rule mismatches.
        /// </summary>
        private (string cacheControl, bool matchesPolicy) GetCacheControlHeader(string requestPath)
        {
            var normalizedPath = requestPath.Replace("\\", "/").ToLower();

            // Match against cache policy matrix from infra §3
            if (Regex.IsMatch(normalizedPath, @"^/[^/]+/bundles/.+\.bundle$"))
            {
                // Bundles: immutable, 1 year cache
                return ("public, max-age=31536000, immutable", true);
            }

            if (Regex.IsMatch(normalizedPath, @"^/[^/]+/catalog/.*/catalog_.*\.hash$"))
            {
                // Catalog hash: must revalidate, short edge TTL
                return ("public, max-age=0, must-revalidate", true);
            }

            if (Regex.IsMatch(normalizedPath, @"^/[^/]+/catalog/.*/catalog_.*\.bin$"))
            {
                // Catalog body: must revalidate, short edge TTL
                return ("public, max-age=0, must-revalidate", true);
            }

            if (Regex.IsMatch(normalizedPath, @"^/[^/]+/catalog/.*/build-manifest\.json$"))
            {
                // Build manifest: never cached (operational metadata)
                return ("no-store", true);
            }

            // Unknown path: default to no-store (safe policy) and flag as non-matching
            // This makes cache rule mismatches loud instead of silent (risk R3)
            return ("no-store", false);
        }

        private string GetContentType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLower();

            return extension switch
            {
                ".bundle" => "application/octet-stream",
                ".bin" => "application/octet-stream",
                ".hash" => "text/plain",
                ".json" => "application/json",
                ".txt" => "text/plain",
                ".html" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }

        private void SendErrorResponse(HttpListenerContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";

            var errorBody = $"{statusCode} {message}";
            var buffer = System.Text.Encoding.UTF8.GetBytes(errorBody);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        }

        private void RaiseRequestEvent(string method, string path, int statusCode, string cacheControl, long bytesSent, bool pathMatchesInfraPolicy)
        {
            RequestReceived?.Invoke(this, new RequestEventArgs
            {
                Method = method,
                Path = path,
                StatusCode = statusCode,
                CacheControl = cacheControl,
                BytesSent = bytesSent,
                Timestamp = DateTime.Now,
                PathMatchesInfraPolicy = pathMatchesInfraPolicy
            });
        }
    }

    /// <summary>
    /// Editor menu for LocalContentServer.
    /// Uses [InitializeOnLoad] to ensure cleanup hooks are registered even if the menu is never touched.
    /// Exposes a shared Instance that both menu and UI tabs use.
    /// </summary>
    [InitializeOnLoad]
    public static class LocalContentServerMenu
    {
        private static LocalContentServer _instance;
        private const int DefaultPort = 8080;

        /// <summary>
        /// Shared server instance used by menu and UI tabs.
        /// </summary>
        public static LocalContentServer Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new LocalContentServer();
                }
                return _instance;
            }
        }

        static LocalContentServerMenu()
        {
            // Initialize server (creates cleanup hooks) even if menu is never touched
            _ = Instance;

            // ...and restart it if it was running before the domain reload that just happened.
            //
            // Entering play mode reloads the domain, which wipes the static holding the server and
            // takes the HttpListener with it. The instance was recreated here, but STOPPED - so from
            // the game's point of view the local CDN simply vanished at the exact moment it started
            // being used, and the symptom is "ConnectionError : Cannot connect to destination host".
            // That reads as a broken CDN, not as a server that quietly died, and on Addressables 2.9.1
            // a failed catalog fetch then poisons ResourceManager.Update for the rest of the session
            // (see CatalogService.WarnAboutCheckCatalogsDefect).
            //
            // delayCall rather than inline: this constructor runs during assembly load, where binding
            // a listener is not safe.
            if (SessionState.GetBool(LocalContentServer.WantsToRunKey, false))
            {
                int port = SessionState.GetInt(LocalContentServer.PortKey, DefaultPort);
                EditorApplication.delayCall += () =>
                {
                    if (Instance.IsRunning) return;
                    if (!SessionState.GetBool(LocalContentServer.WantsToRunKey, false)) return;

                    Debug.Log($"[LocalContentServer] Restarting on port {port} after a domain reload.");
                    Instance.Start(port);
                };
            }
        }

        [MenuItem("Tools/Addressable Manager/Start Local Content Server")]
        private static void StartServer()
        {
            if (!Instance.IsRunning)
            {
                Instance.Start(DefaultPort);
            }
            else
            {
                Debug.Log("LocalContentServer is already running");
            }
        }

        [MenuItem("Tools/Addressable Manager/Stop Local Content Server")]
        private static void StopServer()
        {
            if (Instance.IsRunning)
            {
                Instance.Stop();
            }
            else
            {
                Debug.Log("LocalContentServer is not running");
            }
        }

        [MenuItem("Tools/Addressable Manager/Stop Local Content Server", validate = true)]
        private static bool ValidateStopServer()
        {
            return Instance.IsRunning;
        }
    }
}
