using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;
using YourAI.Core.Net;

namespace YourAI.Transport
{
    /// <summary>
    /// Turns a library-free <see cref="HttpRequestSpec"/> into a UnityWebRequest.
    ///
    /// Shared by both entry points in this assembly -- the full transport and the
    /// minimal SSE client -- so that the fiddly parts are written once. There are
    /// two of them, and both are the kind of thing that works until it does not:
    ///
    /// <para>Content-Type lives on the upload handler, not in the header list.
    /// Setting it as a header as well is not redundant but wrong: the handler's value
    /// wins at send time, so a caller who sets a header would silently get
    /// <c>application/octet-stream</c> instead.</para>
    ///
    /// <para>Unity rejects some header names outright. One unusable header should
    /// cost that header, not the request, so the setter is guarded.</para>
    /// </summary>
    internal static class UnityRequestBuilder
    {
        public static UnityWebRequest Create(
            HttpRequestSpec spec,
            DownloadHandler downloadHandler,
            CertificateHandler certificateHandler)
        {
            string method = string.IsNullOrEmpty(spec.Method) ? "POST" : spec.Method;
            UnityWebRequest request = new UnityWebRequest(spec.Url, method);

            string contentType = "application/json";
            if (spec.Headers != null)
            {
                foreach (KeyValuePair<string, string> header in spec.Headers)
                {
                    if (string.IsNullOrEmpty(header.Key))
                    {
                        continue;
                    }

                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = header.Value;
                        continue;
                    }

                    try
                    {
                        request.SetRequestHeader(header.Key, header.Value);
                    }
                    catch (Exception)
                    {
                        // Unity rejects a handful of header names. Dropping one beats
                        // failing a request that would otherwise have worked.
                    }
                }
            }

            if (!string.IsNullOrEmpty(spec.Body))
            {
                UploadHandlerRaw upload = new UploadHandlerRaw(Encoding.UTF8.GetBytes(spec.Body));
                upload.contentType = contentType;
                request.uploadHandler = upload;
            }

            request.downloadHandler = downloadHandler;

            if (certificateHandler != null)
            {
                request.certificateHandler = certificateHandler;
                // The handler belongs to the caller, not to this request. Leaving the
                // default true would dispose it when the first request finishes and
                // break every request after that.
                request.disposeCertificateHandlerOnDispose = false;
            }

            if (spec.TimeoutSeconds > 0)
            {
                request.timeout = spec.TimeoutSeconds;
            }

            return request;
        }
    }
}
