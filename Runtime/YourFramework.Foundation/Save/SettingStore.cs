using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YourAI.Core.Json;

namespace YourFramework.Save
{
    /// <summary>整体作用于配置文件文本的编解码钩子。null 表示明文。</summary>
    public interface IValueCodec
    {
        /// <summary>Encodes plaintext file content for storage.</summary>
        string Encode(string plain);

        /// <summary>Decodes stored file content, or null if it cannot be decoded.</summary>
        string Decode(string stored);
    }

    /// <summary>
    /// 混淆级保护：XOR + Base64，让存档不是“记事本直接可改”。这不是加密学意义上
    /// 的安全（密钥在客户端就谈不上保密），防的是玩家拿文本编辑器改数值——防君子
    /// 不防逆向，需求若升级为真加密再换实现，接口不变。
    /// </summary>
    public sealed class XorValueCodec : IValueCodec
    {
        private readonly byte[] _key;

        public XorValueCodec(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("XorValueCodec: key must not be empty.", "key");
            }

            _key = Encoding.UTF8.GetBytes(key);
        }

        public string Encode(string plain)
        {
            byte[] data = Encoding.UTF8.GetBytes(plain);
            byte[] encoded = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                encoded[i] = (byte)(data[i] ^ _key[i % _key.Length]);
            }

            return Convert.ToBase64String(encoded);
        }

        public string Decode(string stored)
        {
            try
            {
                byte[] data = Convert.FromBase64String(stored);
                byte[] decoded = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    decoded[i] = (byte)(data[i] ^ _key[i % _key.Length]);
                }

                return Encoding.UTF8.GetString(decoded);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 键值配置：全局只读配置与玩家偏好共用这一个类（清单里“配置管理”的落点）。
    /// 值是 <see cref="JsonValue"/>（标量或嵌套结构），带常用标量的类型化读写。
    ///
    /// 磁盘纪律与 SaveSystem 一致：临时文件 + 替换的原子写；失败也是值——
    /// <see cref="LoadFromDisk"/> / <see cref="SaveToDisk"/> 返回 null 表示成功，
    /// 否则是错误说明。文件不存在按“首次运行”处理（空表，Load 返回 null）。
    ///
    /// 修改只在内存进行，宿主决定何时写进文件（退出时、关键节点、定时）。
    /// 线程：仅限主线程。
    /// </summary>
    public sealed class SettingStore
    {
        private readonly string _filePath;
        private readonly IValueCodec _codec;
        private readonly Dictionary<string, JsonValue> _values =
            new Dictionary<string, JsonValue>();

        /// <summary>
        /// 上次 LoadFromDisk 遇到“文件在、但读不出来”时置位：盘上是什么我们并不知道，
        /// 此时不许存盘把它盖掉。读通了、或文件本来就坏到能判定，都会解除。
        /// </summary>
        private bool _writeBlocked;

        public SettingStore(string filePath, IValueCodec codec = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("SettingStore: file path must not be empty.", "filePath");
            }

            _filePath = filePath;
            _codec = codec;
        }

        /// <summary>Number of keys currently held.</summary>
        public int Count { get { return _values.Count; } }

        public bool Has(string key)
        {
            return key != null && _values.ContainsKey(key);
        }

        // ------------------------------------------------------------- typed get

        public string GetString(string key, string fallback = null)
        {
            JsonValue value;
            return _values.TryGetValue(key, out value) ? value.AsString(fallback) : fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            JsonValue value;
            return _values.TryGetValue(key, out value) ? value.AsInt(fallback) : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            JsonValue value;
            return _values.TryGetValue(key, out value) ? (float)value.AsDouble(fallback) : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            JsonValue value;
            return _values.TryGetValue(key, out value) ? value.AsBool(fallback) : fallback;
        }

        /// <summary>The raw value, or JsonValue.Null when the key is absent.</summary>
        public JsonValue Get(string key)
        {
            JsonValue value;
            return _values.TryGetValue(key, out value) ? value : JsonValue.Null;
        }

        // ------------------------------------------------------------- typed set

        public void SetString(string key, string value)
        {
            _values[key] = new JsonValue { Kind = JsonKind.String, StringValue = value };
        }

        public void SetInt(string key, int value)
        {
            _values[key] = new JsonValue { Kind = JsonKind.Number, NumberValue = value };
        }

        public void SetFloat(string key, float value)
        {
            _values[key] = new JsonValue { Kind = JsonKind.Number, NumberValue = value };
        }

        public void SetBool(string key, bool value)
        {
            _values[key] = new JsonValue { Kind = JsonKind.Bool, BoolValue = value };
        }

        public void Set(string key, JsonValue value)
        {
            if (value == null)
            {
                _values.Remove(key);
                return;
            }

            _values[key] = value;
        }

        /// <summary>Removes a key. Returns whether it existed.</summary>
        public bool Remove(string key)
        {
            return _values.Remove(key);
        }

        /// <summary>Drops every key (memory only; call SaveToDisk to persist).</summary>
        public void Clear()
        {
            _values.Clear();
        }

        // ------------------------------------------------------------------ disk

        /// <summary>
        /// Reads the file into memory. Returns null on success (including
        /// first-run-missing), otherwise an error description and the store stays
        /// usable-but-empty: a corrupt or undecodable file never blocks the game.
        ///
        /// 但“文件在、却读不出来”（被别的进程占着、权限不对）是另一回事：那种情况下
        /// 我们根本不知道盘上是什么，于是会置起写盘封锁，让 <see cref="SaveToDisk"/>
        /// 拒绝覆盖它。常规写法是“启动 Load、退出 Save”，若不设这道防，一次瞬时的读失败
        /// 就会让玩家全部偏好被一个空表盖掉。
        /// </summary>
        public string LoadFromDisk()
        {
            _values.Clear();

            string text;
            try
            {
                if (!File.Exists(_filePath))
                {
                    _writeBlocked = false;
                    return null; // first run
                }

                text = File.ReadAllText(_filePath);
            }
            catch (Exception ex)
            {
                _writeBlocked = true;
                return ex.GetType().Name + ": " + ex.Message;
            }

            // 内容读进来了，好坏都是可判定的：坏文件按“首次运行”处理，解除封锁。
            _writeBlocked = false;

            if (_codec != null)
            {
                text = _codec.Decode(text);
                if (text == null)
                {
                    return "stored content cannot be decoded (wrong codec or corrupted file)";
                }
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(text);
            }
            catch (Exception ex)
            {
                return "not valid JSON: " + ex.Message;
            }

            if (root.Kind != JsonKind.Object)
            {
                return "root must be a JSON object";
            }

            foreach (KeyValuePair<string, JsonValue> pair in root.Members)
            {
                _values[pair.Key] = pair.Value;
            }

            return null;
        }

        /// <summary>
        /// Writes the whole table atomically (tmp + replace). null = success.
        ///
        /// 上次 <see cref="LoadFromDisk"/> 若是“文件读不出来”而失败，这里会**拒绝覆盖**
        /// 并返回原因 —— 那种情况下盘上的内容未知，照写等于拿一个空表盖掉玩家的配置。
        /// 明确要覆盖（例如玩家自己选了“重置设置”）再传 <paramref name="force"/>。
        /// </summary>
        public string SaveToDisk(bool force = false)
        {
            if (_writeBlocked && !force)
            {
                return "refusing to overwrite '" + _filePath
                    + "': the last load could not read it, so what is on disk is unknown"
                    + " (pass force: true to overwrite anyway)";
            }

            try
            {
                JsonValue root = new JsonValue
                {
                    Kind = JsonKind.Object,
                    Members = new Dictionary<string, JsonValue>(_values),
                };

                string text = root.ToJson();
                if (_codec != null)
                {
                    text = _codec.Encode(text);
                }

                string directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, text);
                AtomicFile.Replace(tmp, _filePath, null);

                // 盘上已经是内存这一份了，封锁自然解除。
                _writeBlocked = false;
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
