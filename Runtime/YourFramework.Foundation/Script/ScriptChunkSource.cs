using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YourFramework.Core;

namespace YourFramework.Scripting
{
    /// <summary>
    /// 脚本源码的来源。引擎边界：默认实现读本地文件（<see cref="FileScriptChunkSource"/>），
    /// 想把脚本塞进加密包 / 从内存解压 / 走远程的宿主换一个实现即可。
    ///
    /// 失败是值：读不到就返回 false + 原因，不抛异常 —— 磁盘 IO 与网络 IO 同级，都是运行期故障。
    /// 与程序集字节来源（IAssemblyBytesSource）是同一副姿态，只是这里读的是**文本**：
    /// 所以多一条纪律 —— 编码必须是严格 UTF-8，坏字节要报出来，不许静默变成替换符
    /// （半个 UTF-8 字符的 Lua 源码会以「语法错误」的面目出现，让人查错方向）。
    /// </summary>
    public interface IScriptChunkSource
    {
        /// <summary>来源名，用于诊断与代码块的 Origin 标签。</summary>
        string Name { get; }

        /// <summary>
        /// Reads one script source file as text. Returns false with a reason on failure; never
        /// throws for content (a null <paramref name="error"/> with non-null text on success).
        /// </summary>
        bool TryReadText(string fileName, out string text, out string error);
    }

    /// <summary>
    /// 默认来源：缓存目录下的相对路径。
    ///
    /// 路径纪律与热更程序集共用同一道闸（<see cref="RelativePathGuard"/>）：拒绝绝对路径、
    /// 拒绝任何一段「..」。清单是从网上来的，一个被篡改的清单不能借文件名读到缓存目录之外。
    ///
    /// 两条文件级礼节（都是真实踩过的坑，不是洁癖）：
    /// 1. 严格 UTF-8 解码 —— 坏字节是失败 + 原因，不是替换符；
    /// 2. 剥掉开头的 UTF-8 BOM —— Windows 上的编辑器默认会给 .lua 加 BOM，
    ///    而 Lua 词法器不认 BOM，会把它报成第一行语法错。
    /// </summary>
    public sealed class FileScriptChunkSource : IScriptChunkSource
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Builds a source rooted at a cache directory. A null/empty root is a wiring bug.</summary>
        public FileScriptChunkSource(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException(
                    "FileScriptChunkSource: rootDirectory must not be null or empty.", "rootDirectory");
            }

            RootDirectory = rootDirectory;
        }

        /// <summary>缓存目录（清单里的 FileName 相对它解析）。</summary>
        public string RootDirectory { get; private set; }

        /// <summary>来源名。</summary>
        public string Name { get { return "file"; } }

        /// <summary>Reads one script source from the cache directory.</summary>
        public bool TryReadText(string fileName, out string text, out string error)
        {
            text = null;
            error = null;

            string problem;
            if (!RelativePathGuard.TryValidate(fileName, out problem))
            {
                error = "script file name " + problem;
                return false;
            }

            string path = Path.Combine(RootDirectory, fileName);
            if (!File.Exists(path))
            {
                error = "script file not found: " + path;
                return false;
            }

            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                error = "reading '" + path + "' failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            try
            {
                text = StrictUtf8.GetString(raw);
            }
            catch (DecoderFallbackException ex)
            {
                text = null;
                error = "script file '" + path + "' is not valid UTF-8 at byte " + ex.Index
                    + " (a half-decoded chunk would surface later as an unrelated syntax error)";
                return false;
            }

            text = StripBom(text);
            return true;
        }

        /// <summary>剥掉开头的 BOM。没有就原样返回（零拷贝地返回原引用）。</summary>
        public static string StripBom(string text)
        {
            if (text != null && text.Length > 0 && text[0] == '\uFEFF')
            {
                return text.Substring(1);
            }

            return text;
        }
    }

    /// <summary>
    /// 内存来源：文件名 → 文本。测试替身，也是宿主「把脚本编译进包体」时的实现基线。
    ///
    /// 重复添加同一个文件名是**测试写错了**（想覆盖就显式先 Remove），所以这里 fail-fast ——
    /// 测试替身静默覆盖会让「我明明改了脚本怎么没生效」变成一次无头案。
    /// </summary>
    public sealed class MemoryScriptChunkSource : IScriptChunkSource
    {
        private readonly Dictionary<string, string> _files =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>来源名。</summary>
        public string Name { get { return "memory"; } }

        /// <summary>已登记的文件数。</summary>
        public int Count { get { return _files.Count; } }

        /// <summary>登记一份脚本源码。</summary>
        public void Add(string fileName, string text)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException("MemoryScriptChunkSource.Add: fileName must not be empty.", "fileName");
            }

            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            if (_files.ContainsKey(fileName))
            {
                throw new InvalidOperationException(
                    "MemoryScriptChunkSource.Add: '" + fileName + "' is already registered; remove it first "
                    + "if replacing a chunk was intended.");
            }

            _files.Add(fileName, text);
        }

        /// <summary>撤掉一份。</summary>
        public void Remove(string fileName)
        {
            if (fileName != null)
            {
                _files.Remove(fileName);
            }
        }

        /// <summary>是否有这份文件。</summary>
        public bool Contains(string fileName)
        {
            return fileName != null && _files.ContainsKey(fileName);
        }

        /// <summary>Reads one script source from memory.</summary>
        public bool TryReadText(string fileName, out string text, out string error)
        {
            text = null;
            error = null;

            if (string.IsNullOrEmpty(fileName))
            {
                error = "script file name must not be empty";
                return false;
            }

            string found;
            if (!_files.TryGetValue(fileName, out found))
            {
                error = "script file not found in memory source: " + fileName;
                return false;
            }

            text = found;
            return true;
        }
    }
}
