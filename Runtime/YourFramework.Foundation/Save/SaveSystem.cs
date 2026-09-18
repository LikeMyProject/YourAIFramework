using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YourAI.Core.Json;

namespace YourFramework.Save
{
    /// <summary>One row of <see cref="SaveSystem.ListSlots"/>.</summary>
    public sealed class SaveSlotMeta
    {
        public string Slot;
        public int Version;
        public long SavedAtUnix;
        public long FileSizeBytes;

        public DateTime SavedAtUtc
        {
            get { return DateTimeOffset.FromUnixTimeSeconds(SavedAtUnix).UtcDateTime; }
        }
    }

    /// <summary>
    /// 槽位存档：一槽一文件，JSON 信封包裹任意 JsonValue 载荷。
    ///
    /// 文件布局：{"slot":...,"version":n,"savedAtUnix":n,"payload":{...}}。
    /// payload 是什么、怎么分节（玩家/世界/任务……）由游戏侧决定，本模块只管
    /// 信封与磁盘纪律：
    ///
    /// - 原子写：先写 .tmp 再替换，进程被杀不会留下半个存档；
    /// - 备份轮转：每次 Save 把上一版挪进 .sav.1 ... .sav.{keepBackups}；
    /// - 损坏隔离：读不懂的存档改名 .corrupt 让路（与 FileMemoryStore 同一判断），
    ///   游戏按“存档不存在”处理，玩家还能进新档；
    /// - 失败是值：磁盘故障不抛异常。TryLoad 返回 false、Save/Load 类方法把错误
    ///   原因作为返回值带回，让 UI 有话可说。
    ///
    /// 槽名只允许 [A-Za-z0-9_-]，1..64 字符——它是文件名的一部分，放行路径分隔符
    /// 等于留一个目录穿越洞。违规槽名是程序 bug，抛 ArgumentException。
    ///
    /// 线程：仅限主线程（文件量级小，同步写足够；真要后台再说）。
    /// </summary>
    public sealed class SaveSystem
    {
        private readonly string _directory;
        private readonly int _keepBackups;

        /// <summary>
        /// <param name="directory">存档目录，游戏侧通常给 persistentDataPath 下的子目录。</param>
        /// <param name="keepBackups">每个槽保留的历史版本数，0 = 只留当前版。</param>
        /// </summary>
        public SaveSystem(string directory, int keepBackups = 1)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("SaveSystem: directory must not be empty.", "directory");
            }

            _directory = directory;
            _keepBackups = Math.Max(0, keepBackups);
        }

        public string DirectoryPath { get { return _directory; } }

        /// <summary>写一个槽。返回 null 表示成功，否则是错误说明（失败是值）。</summary>
        public string Save(string slot, JsonValue payload, int version = 1)
        {
            slot = ValidateSlot(slot);
            if (payload == null || !payload.IsObject)
            {
                return "payload must be a JSON object";
            }

            try
            {
                Directory.CreateDirectory(_directory);

                JsonValue envelope = new JsonValue
                {
                    Kind = JsonKind.Object,
                    Members = new Dictionary<string, JsonValue>
                    {
                        { "slot", new JsonValue { Kind = JsonKind.String, StringValue = slot } },
                        { "version", new JsonValue { Kind = JsonKind.Number, NumberValue = version } },
                        { "savedAtUnix", new JsonValue { Kind = JsonKind.Number, NumberValue = NowUnix() } },
                        { "payload", payload },
                    },
                };

                string path = SlotPath(slot);
                string tmp = path + ".tmp";

                RotateBackups(slot, path);

                File.WriteAllText(tmp, envelope.ToJson());
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// 读一个槽。false 时 payload 为 null 且带错误原因；损坏文件会被改名
        /// .corrupt 隔离（只隔离一次，重试不会反复改）。
        /// </summary>
        public bool TryLoad(string slot, out JsonValue payload, out int version, out string error)
        {
            slot = ValidateSlot(slot);
            payload = null;
            version = 0;
            error = null;

            string path = SlotPath(slot);
            if (!File.Exists(path))
            {
                error = "no such save";
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            JsonValue envelope;
            try
            {
                envelope = JsonParser.Parse(text);
            }
            catch (Exception ex)
            {
                Quarantine(slot, path);
                error = "corrupted save (quarantined): " + ex.Message;
                return false;
            }

            JsonValue payloadValue = envelope.Path("payload");
            if (envelope.Kind != JsonKind.Object || payloadValue == null || payloadValue.Kind != JsonKind.Object)
            {
                Quarantine(slot, path);
                error = "corrupted save (quarantined): envelope has no payload object";
                return false;
            }

            version = envelope.Path("version").AsInt(1);
            payload = payloadValue;
            return true;
        }

        /// <summary>Whether the slot's file exists (corrupt files count as absent).</summary>
        public bool Exists(string slot)
        {
            slot = ValidateSlot(slot);
            return File.Exists(SlotPath(slot));
        }

        /// <summary>删除槽文件与全部备份。返回 null 表示成功（含“本来就不存在”）。</summary>
        public string Delete(string slot)
        {
            slot = ValidateSlot(slot);
            try
            {
                DeleteIfExists(SlotPath(slot));
                for (int i = 1; i <= _keepBackups; i++)
                {
                    DeleteIfExists(BackupPath(slot, i));
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>列出当前所有可读的槽（损坏文件不出现；最新版本在前）。</summary>
        public List<SaveSlotMeta> ListSlots()
        {
            List<SaveSlotMeta> result = new List<SaveSlotMeta>();
            if (!Directory.Exists(_directory))
            {
                return result;
            }

            foreach (string file in Directory.GetFiles(_directory, "*.sav"))
            {
                try
                {
                    JsonValue envelope = JsonParser.Parse(File.ReadAllText(file));
                    SaveSlotMeta meta = new SaveSlotMeta();
                    meta.Slot = envelope.Path("slot").AsString(Path.GetFileNameWithoutExtension(file));
                    meta.Version = envelope.Path("version").AsInt(1);
                    meta.SavedAtUnix = (long)envelope.Path("savedAtUnix").AsDouble(0);
                    meta.FileSizeBytes = new FileInfo(file).Length;
                    result.Add(meta);
                }
                catch
                {
                    // Corrupt slot: skipped here, surfaced by TryLoad.
                }
            }

            result.Sort((a, b) => b.SavedAtUnix.CompareTo(a.SavedAtUnix));
            return result;
        }

        // ------------------------------------------------------------------ core

        private void RotateBackups(string slot, string path)
        {
            for (int i = _keepBackups; i >= 1; i--)
            {
                string from = i == 1 ? path : BackupPath(slot, i - 1);
                string to = BackupPath(slot, i);
                if (File.Exists(to))
                {
                    File.Delete(to);
                }

                if (File.Exists(from))
                {
                    File.Move(from, to);
                }
            }
        }

        private void Quarantine(string slot, string path)
        {
            try
            {
                string target = path + ".corrupt";
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(path, target);
            }
            catch
            {
                // Quarantine is best-effort; the load error already told the truth.
            }
        }

        private string SlotPath(string slot)
        {
            return Path.Combine(_directory, slot + ".sav");
        }

        private string BackupPath(string slot, int index)
        {
            return Path.Combine(_directory, slot + ".sav." + index.ToString(CultureInfo.InvariantCulture));
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static string ValidateSlot(string slot)
        {
            if (string.IsNullOrEmpty(slot) || slot.Length > 64)
            {
                throw new ArgumentException("SaveSystem: slot name must be 1..64 chars.", "slot");
            }

            for (int i = 0; i < slot.Length; i++)
            {
                char c = slot[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    throw new ArgumentException(
                        "SaveSystem: slot name may only contain [A-Za-z0-9_-], got '" + slot + "'.", "slot");
                }
            }

            return slot;
        }

        private static long NowUnix()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
