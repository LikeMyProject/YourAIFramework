using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace YourFramework.Localization
{
    /// <summary>Where a locale file request stands. Ready/Failed are terminal and sticky.</summary>
    public enum LocaleFileState
    {
        /// <summary>File read or web request is in flight.</summary>
        Loading,

        /// <summary>Text is available in <see cref="LocaleFileRequest.Json"/>.</summary>
        Ready,

        /// <summary>Load failed; <see cref="LocaleFileRequest.Error"/> says why.</summary>
        Failed
    }

    /// <summary>
    /// One locale file load. A request is a value: it carries the path it tried,
    /// the text on success and a readable reason on failure, so callers can show
    /// something useful instead of guessing.
    /// </summary>
    public sealed class LocaleFileRequest
    {
        internal UnityWebRequest Web;

        internal LocaleFileRequest(string locale, string collection, string tableName, string path)
        {
            Locale = locale;
            Collection = collection;
            TableName = tableName;
            Path = path;
            State = LocaleFileState.Loading;
        }

        /// <summary>Locale tag requested (as asked for, before normalization).</summary>
        public string Locale { get; private set; }

        /// <summary>Collection the table belongs to.</summary>
        public string Collection { get; private set; }

        /// <summary>Table name (defaults to the collection name when not given).</summary>
        public string TableName { get; private set; }

        /// <summary>Full path or URL that was read. Printed in diagnostics.</summary>
        public string Path { get; private set; }

        /// <summary>Current state.</summary>
        public LocaleFileState State { get; internal set; }

        /// <summary>Table text when <see cref="State"/> is Ready, else null.</summary>
        public string Json { get; internal set; }

        /// <summary>Non-empty failure reason when <see cref="State"/> is Failed, else null.</summary>
        public string Error { get; internal set; }

        /// <summary>Whether the request settled (Ready or Failed).</summary>
        public bool IsDone
        {
            get { return State == LocaleFileState.Ready || State == LocaleFileState.Failed; }
        }

        /// <summary>Whether the request settled successfully.</summary>
        public bool IsReady { get { return State == LocaleFileState.Ready; } }

        /// <summary>Whether the request settled with a failure.</summary>
        public bool IsFailed { get { return State == LocaleFileState.Failed; } }
    }

    /// <summary>
    /// 语言文件加载器（引擎侧）：把 <c>&lt;根目录&gt;/&lt;locale&gt;/&lt;名字&gt;.json</c>
    /// 读成文本，交给 <see cref="LocalePack.LoadTable"/> 之类去建表。
    ///
    /// 设计取舍：
    /// 1. **泵语义**：请求发出去后由宿主每帧 Pump 推进，不出现裸 async/await ——
    ///    与框架其余部分（下载、热更、AI）同一条时间模型，暂停/快进/无头测试都成立；
    /// 2. **同步与异步自动选路**：根目录是普通路径（编辑器、PC、iOS）就走
    ///    File.ReadAllText 当场完成；根目录是 URL（Android 的 jar:file://、WebGL 的
    ///    http(s)）就走 UnityWebRequest —— 这两种形态在 StreamingAssets 上真实存在，
    ///    用"根里有没有 ://"来判，比按平台硬编码分支更难写错；
    /// 3. **失败是值**：文件不存在、请求 404、DNS 失败，全部落 Failed 并带上原因，
    ///    不抛异常 —— 语言文件缺失是发行期常态（不是每种语言都备齐），
    ///    让上层决定退到哪种语言继续跑；
    /// 4. 结算过的请求由调用方 TryTakeSettled 取走，加载器自己不无限攒结果。
    /// </summary>
    public sealed class UnityLocaleLoader : IDisposable
    {
        private readonly string _root;
        private readonly List<LocaleFileRequest> _loading = new List<LocaleFileRequest>();
        private readonly List<LocaleFileRequest> _settled = new List<LocaleFileRequest>();
        private bool _disposed;

        /// <summary>
        /// Creates a loader rooted at a directory or URL. Typically
        /// <c>Application.streamingAssetsPath + "/Locale"</c>.
        /// </summary>
        public UnityLocaleLoader(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("UnityLocaleLoader: rootDirectory must not be empty.", "rootDirectory");
            }

            _root = rootDirectory;
        }

        /// <summary>Root directory or URL this loader reads from.</summary>
        public string RootDirectory { get { return _root; } }

        /// <summary>Requests still in flight.</summary>
        public int LoadingCount { get { return _loading.Count; } }

        /// <summary>Settled requests waiting to be taken.</summary>
        public int SettledCount { get { return _settled.Count; } }

        /// <summary>
        /// Starts loading a table. <paramref name="tableName"/> defaults to the
        /// collection name, so a one-table-per-collection layout needs no extra
        /// argument. Returns the request; it is already settled when the root is a
        /// plain path.
        /// </summary>
        public LocaleFileRequest Request(string locale, string collection, string tableName = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("UnityLocaleLoader");
            }

            if (string.IsNullOrEmpty(locale))
            {
                throw new ArgumentException("UnityLocaleLoader.Request: locale must not be empty.", "locale");
            }

            if (string.IsNullOrEmpty(collection))
            {
                throw new ArgumentException("UnityLocaleLoader.Request: collection must not be empty.", "collection");
            }

            string name = string.IsNullOrEmpty(tableName) ? collection : tableName;
            string path = ComposePath(_root, locale, name);
            LocaleFileRequest request = new LocaleFileRequest(locale, collection, name, path);

            if (IsUrl(_root))
            {
                request.Web = UnityWebRequest.Get(path);
                request.Web.SendWebRequest();
                _loading.Add(request);
                return request;
            }

            try
            {
                request.Json = File.ReadAllText(path);
                request.State = LocaleFileState.Ready;
            }
            catch (Exception ex)
            {
                request.State = LocaleFileState.Failed;
                request.Error = "could not read '" + path + "': " + ex.Message;
            }

            _settled.Add(request);
            return request;
        }

        /// <summary>Advances every in-flight request by one frame.</summary>
        public void Pump()
        {
            if (_disposed)
            {
                return;
            }

            for (int i = _loading.Count - 1; i >= 0; i--)
            {
                LocaleFileRequest request = _loading[i];
                UnityWebRequest web = request.Web;
                if (web == null || !web.isDone)
                {
                    continue;
                }

                if (web.result == UnityWebRequest.Result.Success)
                {
                    request.Json = web.downloadHandler != null ? web.downloadHandler.text : null;
                    if (string.IsNullOrEmpty(request.Json))
                    {
                        request.State = LocaleFileState.Failed;
                        request.Error = "empty body from '" + request.Path + "'";
                    }
                    else
                    {
                        request.State = LocaleFileState.Ready;
                    }
                }
                else
                {
                    request.State = LocaleFileState.Failed;
                    request.Error = "request failed for '" + request.Path + "': " + web.error;
                }

                web.Dispose();
                request.Web = null;
                _loading.RemoveAt(i);
                _settled.Add(request);
            }
        }

        /// <summary>
        /// Takes one settled request (Ready or Failed), oldest first. The caller
        /// applies Ready ones and reports Failed ones -- failures are values here,
        /// not exceptions.
        /// </summary>
        public bool TryTakeSettled(out LocaleFileRequest request)
        {
            if (_settled.Count == 0)
            {
                request = null;
                return false;
            }

            request = _settled[0];
            _settled.RemoveAt(0);
            return true;
        }

        /// <summary>Cancels everything in flight; cancelled requests are reported as failures.</summary>
        public void CancelAll()
        {
            for (int i = 0; i < _loading.Count; i++)
            {
                LocaleFileRequest request = _loading[i];
                if (request.Web != null)
                {
                    request.Web.Abort();
                    request.Web.Dispose();
                    request.Web = null;
                }

                request.State = LocaleFileState.Failed;
                request.Error = "cancelled";
                _settled.Add(request);
            }

            _loading.Clear();
        }

        /// <summary>Aborts in-flight requests and drops queued results.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_loading.Count > 0)
            {
                CancelAll();
            }

            _settled.Clear();
            _disposed = true;
        }

        private static bool IsUrl(string root)
        {
            return root.IndexOf("://", StringComparison.Ordinal) >= 0;
        }

        private static string ComposePath(string root, string locale, string name)
        {
            string file = name + ".json";
            if (IsUrl(root))
            {
                // URLs must keep forward slashes and must not be run through
                // Path.Combine (it would inject platform separators).
                string trimmed = root.EndsWith("/", StringComparison.Ordinal)
                    ? root.Substring(0, root.Length - 1)
                    : root;
                return trimmed + "/" + locale + "/" + file;
            }

            return Path.Combine(root, locale, file);
        }
    }
}
