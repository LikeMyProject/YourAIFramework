using System;
using System.IO;
using UnityEngine.Networking;
using YourFramework.Assets.Pack;

namespace YourFramework.UnityRuntime.Assets.Pack
{
    /// <summary>
    /// 自研资源包的下载后端：UnityWebRequest + DownloadHandlerFile（写进文件流式，不把
    /// 整个 bundle 读进内存），支持断点续传（Range + 追加写）。
    ///
    /// 设计要点：
    /// 1. 超时不在这里管 —— 队列统一用 deltaSeconds 计超时，后端把 timeout 设为 0
    ///    （不超时），避免两套时钟互相打架；
    /// 2. 已写进文件字节数**以文件实际长度为准**（不是 UnityWebRequest.downloadedBytes）：
    ///    续传时前者天然包含上次写进文件的部分，是队列唯一需要的事实；
    /// 3. 服务器忽略 Range（回 200 而不是 206）时**判定失败并说明原因**，不做
    ///    "悄悄整包重下"的聪明事 —— 那会让本地文件出现重复字节，是更难查的故障；
    ///    判定之后**把那个坏文件丢掉**并如实报 0 字节：它已经不是目标的合法前缀了，
    ///    拿它的长度当续传偏移，下一次的 Range 起点会越过远端文件末尾，永远换回 416；
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
                // 416: the Range start we asked for is past the end of the real file, so
                // whatever is on disk is not a valid prefix of it. Drop it and start clean
                // -- keeping it would make every later attempt ask for the same impossible
                // offset and burn through MaxAttempts without ever transferring a byte.
                if (slot.ResumeFrom > 0 && request.responseCode == 416)
                {
                    DiscardPartial(slot.SavePath);
                    error = "server rejected the resume offset (HTTP 416): the partial file was "
                        + "not a prefix of the target and has been discarded";
                    receivedBytes = BytesOnDisk(slot.SavePath);
                    return DownloadPoll.Failed;
                }

                // The raw text is preserved: "HTTP 404" and "Cannot resolve destination
                // host" need different fixes, and the UI should be able to say which.
                error = "HTTP " + request.responseCode + " " + (request.error ?? "unknown error");
                receivedBytes = BytesOnDisk(slot.SavePath);
                return DownloadPoll.Failed;
            }

            // A resumed transfer must come back as 206 Partial Content. A 200 means
            // the server ignored Range and re-sent the whole file into an appending
            // handler -- bytes now on disk are duplicated, so refuse rather than
            // hand the loader a corrupt bundle. The corrupt file goes with it: its
            // length is not a resume offset, and reporting it as one is what turned a
            // single bad response into a permanent failure.
            if (slot.ResumeFrom > 0 && request.responseCode != 0 && request.responseCode != 206)
            {
                DiscardPartial(slot.SavePath);
                error = "server ignored the Range request (HTTP " + request.responseCode
                    + " instead of 206): the partial file had duplicate bytes and was discarded";
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

        /// <summary>
        /// Throws away a partial file whose bytes are no longer a valid prefix of the
        /// target. Resuming from such a file is worse than starting over: the Range
        /// start lands past the end of the real file, every later attempt gets a 416,
        /// and the corrupt bytes sit on disk looking like progress.
        ///
        /// Best effort -- if it cannot be deleted, the next attempt reports the real
        /// byte count again and MaxAttempts still stops the loop.
        /// </summary>
        private static void DiscardPartial(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
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
