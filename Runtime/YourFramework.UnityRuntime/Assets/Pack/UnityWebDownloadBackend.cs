using System;
using System.IO;
using UnityEngine.Networking;
using YourFramework.Assets.Pack;

namespace YourFramework.UnityRuntime.Assets.Pack
{
    /// <summary>
    /// 自研资源包的下载后端：UnityWebRequest + DownloadHandlerFile（落盘流式，不把
    /// 整个 bundle 读进内存），支持断点续传（Range + 追加写）。
    ///
    /// 设计要点：
    /// 1. 超时不在这里管 —— 队列统一用 deltaSeconds 计超时，后端把 timeout 设为 0
    ///    （不超时），避免两套时钟互相打架；
    /// 2. 已落盘字节数**以文件实际长度为准**（不是 UnityWebRequest.downloadedBytes）：
    ///    续传时前者天然包含上次落盘的部分，是队列唯一需要的事实；
    /// 3. 服务器忽略 Range（回 200 而不是 206）时**判定失败并说明原因**，不做
    ///    "悄悄整包重下"的聪明事 —— 那会让本地文件出现重复字节，是更难查的故障；
    /// 4. 取消（超时/退出）走 Abort，句柄随之下线，重试会新建一个请求。
    /// </summary>
    public sealed class UnityWebDownloadBackend : IDownloadBackend
    {
        private sealed class Slot
        {
            public UnityWebRequest Request;
            public string SavePath;
            public long ResumeFrom;
        }

        /// <summary>Backend name (diagnostics).</summary>
        public string Name { get { return "UnityWebRequest"; } }

        /// <summary>
        /// Starts one attempt. Returns null with a reason when the attempt could not
        /// even be launched (bad directory, transport refused).
        /// </summary>
        public object Begin(string url, string savePath, long resumeFrom, out string error)
        {
            error = null;

            try
            {
                string directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                error = "cannot create the download directory for '" + savePath + "': "
                    + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            bool resuming = resumeFrom > 0;
            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(url);
                request.downloadHandler = new DownloadHandlerFile(savePath, resuming);
                request.timeout = 0; // the queue owns timeouts
                if (resuming)
                {
                    request.SetRequestHeader("Range", "bytes=" + resumeFrom + "-");
                }

                request.SendWebRequest();
            }
            catch (Exception ex)
            {
                if (request != null)
                {
                    request.Dispose();
                }

                error = "cannot start the download of '" + url + "': "
                    + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            return new Slot { Request = request, SavePath = savePath, ResumeFrom = resumeFrom };
        }

        /// <summary>
        /// Advances one attempt. Reports bytes on disk (absolute, resumes included),
        /// which is exactly what the queue needs to carry a partial transfer across
        /// retries.
        /// </summary>
        public DownloadPoll Poll(object handle, out long receivedBytes, out string error)
        {
            receivedBytes = 0;
            error = null;

            Slot slot = handle as Slot;
            if (slot == null || slot.Request == null)
            {
                error = "invalid download handle";
                return DownloadPoll.Failed;
            }

            UnityWebRequest request = slot.Request;
            if (!request.isDone)
            {
                receivedBytes = BytesOnDisk(slot.SavePath);
                return DownloadPoll.Running;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                // The raw text is preserved: "HTTP 404" and "Cannot resolve destination
                // host" need different fixes, and the UI should be able to say which.
                error = "HTTP " + request.responseCode + " " + (request.error ?? "unknown error");
                receivedBytes = BytesOnDisk(slot.SavePath);
                return DownloadPoll.Failed;
            }

            // A resumed transfer must come back as 206 Partial Content. A 200 means
            // the server ignored Range and re-sent the whole file into an appending
            // handler -- bytes now on disk are duplicated, so refuse rather than
            // hand the loader a corrupt bundle.
            if (slot.ResumeFrom > 0 && request.responseCode != 0 && request.responseCode != 206)
            {
                error = "server ignored the Range request (HTTP " + request.responseCode
                    + " instead of 206): partial file would be corrupted";
                receivedBytes = BytesOnDisk(slot.SavePath);
                return DownloadPoll.Failed;
            }

            receivedBytes = BytesOnDisk(slot.SavePath);
            if (receivedBytes <= 0)
            {
                error = "download reported success but the file is empty or missing: " + slot.SavePath;
                return DownloadPoll.Failed;
            }

            return DownloadPoll.Done;
        }

        /// <summary>Aborts one attempt. Safe to call once per handle; never throws.</summary>
        public void Cancel(object handle)
        {
            Slot slot = handle as Slot;
            if (slot == null || slot.Request == null)
            {
                return;
            }

            try
            {
                slot.Request.Abort();
            }
            catch (Exception)
            {
                // Abort during teardown is best-effort by design.
            }

            try
            {
                slot.Request.Dispose();
            }
            catch (Exception)
            {
            }

            slot.Request = null;
        }

        private static long BytesOnDisk(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
