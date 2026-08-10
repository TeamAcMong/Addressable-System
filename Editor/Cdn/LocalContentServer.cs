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

        public LocalContentServer()
        {
            _serverDataPath = Path.Combine(Directory.GetCurrentDirectory(), "ServerData");
            RegisterCleanupHooks();
        }

        private void RegisterCleanupHooks()
        {
            // Clean up before domain reload (recompile)
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;

            // Clean up when editor quits
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
        }

        public void Start(int port)
        {
            if (_isRunning)
            {
                Debug.LogWarning("LocalContentServer is already running");
                return;
            }

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

        public void Stop()
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
                var filePath = Path.Combine(_serverDataPath, requestPath.TrimStart('/'));

                // Security: prevent directory traversal
                var fullPath = Path.GetFullPath(filePath);
                var fullServerPath = Path.GetFullPath(_serverDataPath);
                if (!fullPath.StartsWith(fullServerPath))
                {
                    SendErrorResponse(context, 403, "Forbidden");
                    RaiseRequestEvent(request.HttpMethod, requestPath, 403, null);
                    return;
                }

                if (!File.Exists(filePath))
                {
                    SendErrorResponse(context, 404, "Not Found");
                    RaiseRequestEvent(request.HttpMethod, requestPath, 404, null);
                    return;
                }

                var (cacheControl, matchesPolicy) = GetCacheControlHeader(requestPath);
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
                        fileStream.CopyTo(response.OutputStream);
                    }

                    RaiseRequestEvent(request.HttpMethod, requestPath, 200, cacheControl, fileSize, matchesPolicy);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing request: {ex.Message}");
                try
                {
                    SendErrorResponse(context, 500, "Internal Server Error");
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

        private void RaiseRequestEvent(string method, string path, int statusCode, string cacheControl, long bytesSent = 0, bool pathMatchesInfraPolicy = true)
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
    /// </summary>
    [InitializeOnLoad]
    public static class LocalContentServerMenu
    {
        private static LocalContentServer _server;
        private const int DefaultPort = 8080;

        static LocalContentServerMenu()
        {
            // Initialize server (creates cleanup hooks) even if menu is never touched
            if (_server == null)
            {
                _server = new LocalContentServer();
            }
        }

        [MenuItem("Tools/Addressable Manager/Start Local Content Server")]
        private static void StartServer()
        {
            if (_server == null)
            {
                _server = new LocalContentServer();
            }

            if (!_server.IsRunning)
            {
                _server.Start(DefaultPort);
            }
            else
            {
                Debug.Log("LocalContentServer is already running");
            }
        }

        [MenuItem("Tools/Addressable Manager/Stop Local Content Server")]
        private static void StopServer()
        {
            if (_server?.IsRunning == true)
            {
                _server.Stop();
            }
            else
            {
                Debug.Log("LocalContentServer is not running");
            }
        }

        [MenuItem("Tools/Addressable Manager/Stop Local Content Server", validate = true)]
        private static bool ValidateStopServer()
        {
            return _server?.IsRunning == true;
        }
    }
}
