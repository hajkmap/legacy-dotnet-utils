using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Configuration;
using log4net;
using System.IO.Compression;

namespace Proxy.Controllers
{
    public class MyActionResult : ActionResult
    {
        public override void ExecuteResult(ControllerContext context)
        {
        }
    }

    public class GeoServerController : Controller
    {
        ILog _log = LogManager.GetLogger(typeof(MyActionResult));
        // Static -> only read once from Web.config
        static private string _headerAttributeName, _localhostServer, _proxyBaseUrl;
        static private int _removeDomainFromUserName = -1; // -1 = not initialized from Web.config. 0 = Do not remove, 1 = Remove

        private string GetlocalhostServer()
        {
            if (_localhostServer == null)
            {
                _localhostServer = ConfigurationManager.AppSettings["localhostServer"];
                if (_localhostServer == null)
                    throw new Exception("No \"localhostServer\" set in Web.config");
            }
            return _localhostServer;
        }

        private string GetUserNameForHeader(string userName)
        {
            if (_removeDomainFromUserName == -1)
                _removeDomainFromUserName = ConfigurationManager.AppSettings["removeDomainNameFromUser"] == null ? 0 : int.Parse(ConfigurationManager.AppSettings["removeDomainNameFromUser"]);
            if (_removeDomainFromUserName == 1)
            {
                int n = userName.IndexOf("\\");
                userName = userName.Substring(n < 0 ? 0 : n + 1);
            }
            return userName;
        }

        private string GetHeaderAttributeName()
        {
            if (_headerAttributeName == null)
            {
                _headerAttributeName = ConfigurationManager.AppSettings["headerAttributeName"];
                if (_headerAttributeName == null)
                {
                    _headerAttributeName = "X-Control-Header";
                    _log.DebugFormat("No config found in Web.config for 'headerAttributeName'. Using: {0}", _headerAttributeName);
                }
            }
            return _headerAttributeName;
        }

        private string GetProxyBaseUrl()
        {
            if (_proxyBaseUrl == null)
            {
                _proxyBaseUrl = ConfigurationManager.AppSettings["proxyBaseUrl"];
                if (_proxyBaseUrl == null)
                {
                    _proxyBaseUrl = "";
                    _log.DebugFormat("No config found in Web.config for 'proxyBaseUrl'. No replacement of 'localhostServer' is performed");
                }
            }
            return _proxyBaseUrl;
        }

        private async Task DoMethod(string method, string urlPath, string queryString, byte[] body, string contentType)
        {
            try
            {
                _log.DebugFormat("DoMethod incomig method: {0}, urlPath: {1}, queryString, {2}, contentType: {3}, body length: {4}", method, urlPath, queryString, contentType, body == null ? 0 : body.Length);

                string url = GetlocalhostServer() + urlPath;
                if (!string.IsNullOrEmpty(queryString))
                    url = url + "?" + queryString;
                _log.DebugFormat("DoMethod outgoing url: {0}", url);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.CookieContainer = new CookieContainer();
                request.UseDefaultCredentials = true;
                request.Method = method;

                if (User.Identity.IsAuthenticated) // Add HTTP Header of authenticated user
                {
                    _log.DebugFormat("User(in): {0}", User.Identity.Name);
                    string userName = GetUserNameForHeader(User.Identity.Name);
                    _log.DebugFormat("User(out): {0}", userName);

                    string headerAttributeName = GetHeaderAttributeName();
                    _log.DebugFormat("HeaderAttributeName: {0}", headerAttributeName);

                    request.Headers.Add(headerAttributeName, userName);
                }

                if (method == "POST")
                {
                    // Forward the body bytes and Content-Type unchanged: WFS POST can be XML (GetFeature, Transaction)
                    // or form-encoded KVP, and re-encoding the body would break non-UTF-8 XML
                    request.ContentType = string.IsNullOrEmpty(contentType) ? "text/xml" : contentType;
                    request.ContentLength = body.Length;

                    Stream requestStream = request.GetRequestStream();
                    requestStream.Write(body, 0, body.Length);
                    requestStream.Close();
                }

                await this.HandleResponse(request);
            }
            catch (Exception e)
            {
                _log.FatalFormat("Exception in DoMethod: {0}", e.Message);
                throw e;
            }
        }

        private async Task HandleResponse(HttpWebRequest request)
        {
            if (request != null)
            {
                // Only the request itself is inside try: once CopyResponse has started streaming,
                // the headers are sent and the status code can no longer be changed
                WebResponse resp;
                try
                {
                    resp = await request.GetResponseAsync();
                }
                catch (WebException e)
                {
                    _log.WarnFormat("Exception in HandleResponse: Status: {0}, Message: {1}", e.Status, e.Message);
                    Response.StatusCode = GetHttpStatusCode(e);
                    var errorResp = e.Response as HttpWebResponse;
                    Response.StatusDescription = errorResp != null && !string.IsNullOrEmpty(errorResp.StatusDescription) ? errorResp.StatusDescription : e.Message;
                    if (e.Response == null)
                        return;
                    // Pass on GeoServer's error body, e.g. the OGC ExceptionReport that WFS 2.0 returns with HTTP 400.
                    // Otherwise IIS replaces the body of 4xx/5xx responses with its own error page
                    Response.TrySkipIisCustomErrors = true;
                    resp = e.Response;
                }
                using (resp)
                {
                    CopyResponse(resp);
                }
            }
        }

        private void CopyResponse(WebResponse resp)
        {
            const int BUFFER_SIZE = 1024 * 1024;

            // Set all headers before writing the body: with BufferOutput off they are sent on the first write
            // Copy GeoWebCache Headers
            foreach (var headerName in resp.Headers.AllKeys)
            {
                if (headerName.StartsWith("geowebcache"))
                {
                    Response.Headers.Add(headerName, resp.Headers[headerName]);
                }
            }
            // File name for downloads, e.g. WFS outputFormat=shape-zip or csv
            var contentDisposition = resp.Headers["Content-Disposition"];
            if (!string.IsNullOrEmpty(contentDisposition))
            {
                Response.Headers.Add("Content-Disposition", contentDisposition);
            }
            // Pass on GeoServer's client cache time (Publishing tab / GWC expireClients).
            // Set it through Response.Cache: ASP.NET overwrites a Cache-Control header added via Response.Headers.
            var cacheControl = resp.Headers["Cache-Control"];
            var maxAge = cacheControl == null ? null : Regex.Match(cacheControl, @"max-age=(\d+)");
            if (maxAge != null && maxAge.Success)
            {
                // Private: responses can depend on the user sent in headerAttributeName, so no shared caches
                Response.Cache.SetCacheability(HttpCacheability.Private);
                Response.Cache.SetMaxAge(TimeSpan.FromSeconds(int.Parse(maxAge.Groups[1].Value)));
                if (cacheControl.Contains("must-revalidate"))
                    Response.Cache.SetRevalidation(HttpCacheRevalidation.AllCaches);
            }

            using (var stream = resp.GetResponseStream())
            {
                if (GetProxyBaseUrl().Length > 0 &&
                    (resp.ContentType.StartsWith("application/vnd.ogc.wms_xml") ||
                    resp.ContentType.StartsWith( "text/xml") ||
                    resp.ContentType.StartsWith("application/xml")))
                {
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    byte[] bytes = ms.ToArray();

                    _log.DebugFormat("Response handled the new way, ContentType={0}", resp.ContentType);

                    _log.DebugFormat("Response - content-encodig: {0}", resp.Headers["content-encoding"]);

                    bool compressionUsed = resp.Headers["content-encoding"] == "gzip" ? true : false;
                    if (compressionUsed)
                    {
                        bytes = Compress(bytes, CompressionMode.Decompress);
                    }
                    var strResponse = Encoding.UTF8.GetString(bytes); // Assume that GeoServer returns GetCapabilities in UTF8. Maybe add this to Web.config
                    //_log.DebugFormat("Response stream, before replacement: {0}", strResponse);

                    var strResponseReplaced = strResponse.Replace(GetlocalhostServer(), GetProxyBaseUrl());
                    _log.DebugFormat("Response stream, after replacement: {0}", strResponseReplaced);

                    byte[] byteResponseReplaced = Encoding.UTF8.GetBytes(strResponseReplaced);
                    if (compressionUsed)
                    {
                        byteResponseReplaced = Compress(byteResponseReplaced, CompressionMode.Compress);
                    }

                    // Keep GeoServer's type for WFS, e.g. "text/xml; subtype=gml/3.1.1", which clients use to pick the GML version
                    Response.ContentType = resp.ContentType.StartsWith("application/vnd.ogc.wms_xml") ? "text/xml" : resp.ContentType;
                    Response.OutputStream.Write(byteResponseReplaced, 0, byteResponseReplaced.Length);
                }
                else
                {
                    _log.DebugFormat("Response handled the old way, ContentType={0}", resp.ContentType);

                    Response.ContentType = resp.ContentType;
                    // Stream large responses (e.g. WFS GetFeature) to the client instead of holding them in memory first
                    Response.BufferOutput = false;
                    var bytes = new byte[BUFFER_SIZE];
                    while (true)
                    {
                        var n = stream.Read(bytes, 0, BUFFER_SIZE);
                        if (n == 0)
                        {
                            break;
                        }
                        Response.OutputStream.Write(bytes, 0, n);
                    }
                }
            }
        }

        private byte[] Compress(byte[] inByteArray, CompressionMode mode)
        {
            // Create a GZIP stream with decompression mode.
            // ... Then create a buffer and write into while reading from the GZIP stream.
            using (GZipStream stream = new GZipStream(new MemoryStream(inByteArray), CompressionMode.Decompress))
            {
                const int size = 4096;
                byte[] buffer = new byte[size];
                using (MemoryStream memory = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = stream.Read(buffer, 0, size);
                        if (count > 0)
                        {
                            memory.Write(buffer, 0, count);
                        }
                    }
                    while (count > 0);
                    return memory.ToArray();
                }
            }
        }

        private int GetHttpStatusCode(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                return (int)response.StatusCode;
            }

            return 500;
        }

        /// <summary>
        /// Handle all HTTP-methods
        /// </summary>
        /// <param name="url"></param>
        public async Task<MyActionResult> EndPoint(string url)
        {
            _log.DebugFormat("EndPoint url: {0}", url);

            if (!string.IsNullOrEmpty(url) && url.StartsWith("web/"))
            {
                _log.Warn("Not allowed to use GeoServer Web-interface through proxy: {0}");
                Response.StatusCode = 400;
                Response.StatusDescription = "Not allowed to use GeoServer Web-interface";
            }
            else if (Request.HttpMethod != "GET" && Request.HttpMethod != "POST")
            {
                _log.WarnFormat("EndPoint called with not supported HTTP method: {0}", Request.HttpMethod);
                Response.StatusCode = 405;
                Response.StatusDescription = "Not supported HTTP method";
            }
            else
            {
                byte[] body = null;

                if (Request.HttpMethod == "POST")
                {
                    using (var ms = new MemoryStream())
                    {
                        // MVC model binding may already have read the stream (e.g. Request.Form for form-encoded POSTs)
                        Request.InputStream.Position = 0;
                        Request.InputStream.CopyTo(ms);
                        body = ms.ToArray();
                    }
                }
                // Raw query string: Request.QueryString.ToString() decodes and re-encodes it, which can alter filter values
                await this.DoMethod(Request.HttpMethod, url, Request.ServerVariables["QUERY_STRING"], body, Request.ContentType);
            }
            return new MyActionResult();
        }
    }
}
