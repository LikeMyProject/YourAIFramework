using System;
using System.Collections.Generic;

namespace YourFramework.Assets.Pack
{
    /// <summary>One download's lifecycle.</summary>
    public enum DownloadState
    {
        /// <summary>Waiting for a concurrency slot or a retry delay to elapse.</summary>
        Queued,
        /// <summary>In flight at the backend.</summary>
        Downloading,
        /// <summary>Bytes are on disk.</summary>
        Done,
        /// <summary>Gave up after MaxAttempts; <see cref="DownloadTask.Error"/> says why.</summary>
        Failed,
    }

    /// <summary>What a backend reports for one in-flight download.</summary>
    public enum DownloadPoll
    {
        /// <summary>Still transferring.</summary>
        Running,
        /// <summary>Finished; the bytes are on disk.</summary>
        Done,
        /// <summary>This attempt failed; the queue decides whether to retry.</summary>
        Failed,
    }

    /// <summary>
    /// The engine edge of the downloader. Implementations own the transport
    /// (UnityWebRequest, sockets) and the partial file.
    ///
    /// Contract:
    /// - Begin returns a handle, or null with a non-empty reason (failure as value:
    ///   a refused connection is routine, not exceptional);
    /// - Poll reports the **absolute** number of bytes persisted for this save path,
    ///   retries included, so the queue never has to guess how much of a resumed
    ///   transfer landed. It is a current measurement, not a high-water mark: a backend
    ///   that discards a partial which is no longer a valid prefix of the target reports
    ///   the smaller number (usually 0), and the queue resumes from that. Reporting the
    ///   old, larger value as a resume offset asks the server for a Range it cannot
    ///   satisfy;
    /// - Cancel must be safe to call once per handle and must not throw;
    /// - a backend that throws is the queue's problem to contain: it becomes a
    ///   failed attempt with the exception's reason.
    /// </summary>
    public interface IDownloadBackend
    {
        /// <summary>Backend name (diagnostics).</summary>
        string Name { get; }

        /// <summary>Starts one attempt. Null handle means it failed to start.</summary>
        object Begin(string url, string savePath, long resumeFrom, out string error);

        /// <summary>Advances one attempt.</summary>
        DownloadPoll Poll(object handle, out long receivedBytes, out string error);

        /// <summary>Aborts one attempt (timeout, shutdown).</summary>
        void Cancel(object handle);
    }

    /// <summary>One bundle's download.</summary>
    public sealed class DownloadTask
    {
        /// <summary>Bundle this download belongs to.</summary>
        public string Bundle;

        /// <summary>Source URL.</summary>
        public string Url;

        /// <summary>Where the bytes go.</summary>
        public string SavePath;

        /// <summary>Declared size (progress denominator).</summary>
        public long Size;

        /// <summary>Current state.</summary>
        public DownloadState State;

        /// <summary>Bytes persisted so far (across attempts).</summary>
        public long Received;

        /// <summary>Attempts started so far.</summary>
        public int Attempts;

        /// <summary>Last reason (set on failure; kept while retrying for diagnostics).</summary>
        public string Error;

        /// <summary>Whether the bytes are on disk.</summary>
        public bool IsDone { get { return State == DownloadState.Done; } }

        /// <summary>Whether the queue gave up.</summary>
        public bool IsFailed { get { return State == DownloadState.Failed; } }

        /// <summary>Whether the task still has work to do.</summary>
        public bool IsPending
        {
            get { return State == DownloadState.Queued || State == DownloadState.Downloading; }
        }

        /// <summary>Backend handle while in flight; internal wiring.</summary>
        internal object Handle;

        /// <summary>Seconds this attempt has been running (timeout accounting).</summary>
        internal float AttemptElapsed;

        /// <summary>Queue time (seconds since construction) before which the task must not start.</summary>
        internal float RetryAt;

        /// <summary>Bytes already on disk when the next attempt starts (resume hint).</summary>
        internal long ResumeFrom;
    }

    /// <summary>
    /// 下载队列：并发上限 + 指数退避重试 + 超时截停 + 断点续传，逐帧驱动。
    ///
    /// 设计取舍：
    /// 1. 语义对齐框架必须守的规矩 —— 没有 async/await，每帧推进一帧的量；时间由调用方
    ///    用 deltaSeconds 注入，因此超时/退避可在无头环境里被精确检查项；
    /// 2. 失败也是值 —— 每次尝试失败只是"这次没成"，连败到 MaxAttempts 才落 Failed
    ///    并把最后一次原因原文带出来；队列永远不抛（除了接线 bug：空 URL、重名任务）；
    /// 3. 断点续传靠"后端报告已写进文件字节数"这一个事实源：队列把已写进文件字节作为下次
    ///    尝试的 resumeFrom，不再自作聪明地拼接偏移。这个数字是**当前**有效字节数而非
    ///    历史最大值 —— 后端丢掉失去意义的半成品时会报 0，队列必须跟着退，否则坏偏移
    ///    会被当成续传点一路传下去；
    /// 4. 热路径零分配：派发与轮询都是对内部列表的单遍扫描，无 LINQ、无快照。
    /// </summary>
    public sealed class DownloadQueue
    {
        private readonly List<DownloadTask> _tasks = new List<DownloadTask>();
        private readonly Dictionary<string, DownloadTask> _byBundle =
            new Dictionary<string, DownloadTask>(StringComparer.Ordinal);
        private readonly IDownloadBackend _backend;

        private int _maxConcurrent = 3;
        private int _maxAttempts = 3;
        private float _retryBackoffSeconds = 0.5f;
        private float _timeoutSeconds = 30f;
        private float _elapsed;

        /// <summary>Builds the queue over a transport backend.</summary>
        public DownloadQueue(IDownloadBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException("backend");
            }

            _backend = backend;
        }

        /// <summary>The backend serving this queue.</summary>
        public IDownloadBackend Backend { get { return _backend; } }

        /// <summary>Simultaneous in-flight downloads.</summary>
        public int MaxConcurrent
        {
            get { return _maxConcurrent; }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentException("DownloadQueue.MaxConcurrent: must be >= 1.", "value");
                }

                _maxConcurrent = value;
            }
        }

        /// <summary>Attempts per task before it is declared failed.</summary>
        public int MaxAttempts
        {
            get { return _maxAttempts; }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentException("DownloadQueue.MaxAttempts: must be >= 1.", "value");
                }

                _maxAttempts = value;
            }
        }

        /// <summary>Base retry delay; attempt N waits base * 2^(N-1), capped at 30s.</summary>
        public float RetryBackoffSeconds
        {
            get { return _retryBackoffSeconds; }
            set
            {
                if (value < 0f)
                {
                    throw new ArgumentException("DownloadQueue.RetryBackoffSeconds: must be >= 0.", "value");
                }

                _retryBackoffSeconds = value;
            }
        }

        /// <summary>Per-attempt timeout; exceeding it cancels the attempt and counts as a failure.</summary>
        public float TimeoutSeconds
        {
            get { return _timeoutSeconds; }
            set
            {
                if (value <= 0f)
                {
                    throw new ArgumentException("DownloadQueue.TimeoutSeconds: must be > 0.", "value");
                }

                _timeoutSeconds = value;
            }
        }

        /// <summary>Tasks in declaration order.</summary>
        public List<DownloadTask> Tasks { get { return _tasks; } }

        /// <summary>Tasks still queued or in flight.</summary>
        public int PendingCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_tasks[i].IsPending)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Tasks in flight right now.</summary>
        public int ActiveCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_tasks[i].State == DownloadState.Downloading)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Tasks whose bytes are on disk.</summary>
        public int DoneCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_tasks[i].State == DownloadState.Done)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Tasks the queue gave up on.</summary>
        public int FailedCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_tasks[i].IsFailed)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Nothing queued and nothing in flight.</summary>
        public bool IsIdle { get { return PendingCount == 0; } }

        /// <summary>Whether any task gave up.</summary>
        public bool HasFailures { get { return FailedCount > 0; } }

        /// <summary>Declared total bytes across all tasks.</summary>
        public long TotalBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    total += _tasks[i].Size;
                }

                return total;
            }
        }

        /// <summary>Bytes persisted across all tasks (progress numerator).</summary>
        public long ReceivedBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    total += _tasks[i].Received;
                }

                return total;
            }
        }

        /// <summary>
        /// First failure reason in declaration order, or null. One line the UI can
        /// print without walking the task list.
        /// </summary>
        public string FirstFailureReason
        {
            get
            {
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_tasks[i].IsFailed)
                    {
                        return _tasks[i].Bundle + ": "
                            + (_tasks[i].Error ?? "unknown failure");
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Adds a task. Empty URL/save path and duplicate bundle names are wiring
        /// bugs: fail fast rather than downloading nonsense.
        /// </summary>
        public DownloadTask Enqueue(string bundle, string url, string savePath, long size)
        {
            if (string.IsNullOrEmpty(bundle))
            {
                throw new ArgumentException("DownloadQueue.Enqueue: bundle must not be empty.", "bundle");
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("DownloadQueue.Enqueue: url must not be empty.", "url");
            }

            if (string.IsNullOrEmpty(savePath))
            {
                throw new ArgumentException("DownloadQueue.Enqueue: savePath must not be empty.", "savePath");
            }

            if (_byBundle.ContainsKey(bundle))
            {
                throw new ArgumentException(
                    "DownloadQueue.Enqueue: bundle '" + bundle + "' is already queued.", "bundle");
            }

            DownloadTask task = new DownloadTask
            {
                Bundle = bundle,
                Url = url,
                SavePath = savePath,
                Size = size,
                State = DownloadState.Queued,
                Received = 0,
                Attempts = 0,
                RetryAt = 0f,
                ResumeFrom = 0,
            };

            _tasks.Add(task);
            _byBundle.Add(bundle, task);
            return task;
        }

        /// <summary>
        /// Advances one frame: dispatch queued tasks under the concurrency cap,
        /// then poll the in-flight ones, applying retry/backoff and timeouts.
        /// </summary>
        public void Pump(float deltaSeconds)
        {
            if (deltaSeconds < 0f)
            {
                deltaSeconds = 0f;
            }

            _elapsed += deltaSeconds;

            // Pass 1 -- dispatch. The cap counts tasks already in flight.
            int active = ActiveCount;
            for (int i = 0; i < _tasks.Count && active < _maxConcurrent; i++)
            {
                DownloadTask task = _tasks[i];
                if (task.State != DownloadState.Queued || _elapsed < task.RetryAt)
                {
                    continue;
                }

                string error;
                object handle;
                try
                {
                    handle = _backend.Begin(task.Url, task.SavePath, task.ResumeFrom, out error);
                }
                catch (Exception ex)
                {
                    handle = null;
                    error = "backend '" + _backend.Name + "' threw on Begin: "
                        + ex.GetType().Name + ": " + ex.Message;
                }

                // The attempt counts whether or not the transport managed to start:
                // a backend that refuses every Begin must still exhaust MaxAttempts
                // instead of retrying forever.
                task.Attempts = task.Attempts + 1;

                if (handle == null)
                {
                    FailAttempt(task, string.IsNullOrEmpty(error) ? "backend returned no handle" : error);
                    continue;
                }

                task.Handle = handle;
                task.AttemptElapsed = 0f;
                task.State = DownloadState.Downloading;
                active++;
            }

            // Pass 2 -- poll. Backwards so state changes cannot disturb the scan.
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                DownloadTask task = _tasks[i];
                if (task.State != DownloadState.Downloading)
                {
                    continue;
                }

                long received;
                string error;
                DownloadPoll poll;
                try
                {
                    poll = _backend.Poll(task.Handle, out received, out error);
                }
                catch (Exception ex)
                {
                    poll = DownloadPoll.Failed;
                    received = task.Received;
                    error = "backend '" + _backend.Name + "' threw on Poll: "
                        + ex.GetType().Name + ": " + ex.Message;
                }

                // 后端的数字是事实，不是历史最大值：它丢掉一个"已经不再是合法前缀"的
                // 半成品时（见 Range 被忽略/416 那两条路），会诚实地报 0。这里若按
                // "只许涨"把它挡回去，那个死掉的偏移就会跟着进下一次的 Range 头，
                // 换来 416，再被记下来，一直重试到耗尽。
                task.Received = received < 0 ? 0 : received;

                if (poll == DownloadPoll.Done)
                {
                    task.Handle = null;
                    task.State = DownloadState.Done;
                    task.Error = null;
                    continue;
                }

                if (poll == DownloadPoll.Failed)
                {
                    FailAttempt(task, string.IsNullOrEmpty(error) ? "download failed" : error);
                    continue;
                }

                task.AttemptElapsed += deltaSeconds;
                if (task.AttemptElapsed > _timeoutSeconds)
                {
                    try
                    {
                        _backend.Cancel(task.Handle);
                    }
                    catch (Exception)
                    {
                        // A cancel that throws must not mask the timeout itself.
                    }

                    FailAttempt(task, "timeout after " + _timeoutSeconds + "s");
                }
            }
        }

        /// <summary>
        /// Resets every task back to Queued (keeping URLs and paths) so the same
        /// queue can be re-run. Live runs must be idle first: re-running while
        /// transfers are in flight is a wiring bug.
        /// </summary>
        public void Reset()
        {
            if (!IsIdle)
            {
                throw new InvalidOperationException(
                    "DownloadQueue.Reset: downloads are still pending; wait for idle.");
            }

            for (int i = 0; i < _tasks.Count; i++)
            {
                DownloadTask task = _tasks[i];
                task.State = DownloadState.Queued;
                task.Received = 0;
                task.Attempts = 0;
                task.Error = null;
                task.RetryAt = 0f;
                task.ResumeFrom = 0;
                task.AttemptElapsed = 0f;
                task.Handle = null;
            }

            _elapsed = 0f;
        }

        private void FailAttempt(DownloadTask task, string reason)
        {
            task.Handle = null;
            task.Error = reason;

            if (task.Attempts >= _maxAttempts)
            {
                task.State = DownloadState.Failed;
                return;
            }

            // Retry: keep what landed on disk and let the next attempt resume
            // from there. Backoff doubles per attempt, capped so a long outage
            // does not push the next attempt into the far future.
            float delay = _retryBackoffSeconds;
            for (int i = 1; i < task.Attempts; i++)
            {
                delay *= 2f;
                if (delay >= 30f)
                {
                    delay = 30f;
                    break;
                }
            }

            task.ResumeFrom = task.Received;
            task.State = DownloadState.Queued;
            task.AttemptElapsed = 0f;
            task.RetryAt = _elapsed + delay;
        }
    }
}
