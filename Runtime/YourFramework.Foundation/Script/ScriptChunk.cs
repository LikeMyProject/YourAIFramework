using System;
using System.Text;
using YourFramework.Assets.Pack;

namespace YourFramework.Scripting
{
    /// <summary>
    /// 脚本块的用途分类。
    ///
    /// 与地址目录的 <c>CatalogKind</c> 同一条纪律：分类是**构建期产物**的一部分，
    /// 解析到未知分类一律 fail-fast —— 拼错的分类名不该拖到运行期才以「装不上」的面目出现。
    /// </summary>
    public enum ScriptChunkKind
    {
        /// <summary>主模块：随包常驻的脚本（首包内容）。</summary>
        Module,

        /// <summary>热更补丁：覆盖同名主模块的增量脚本。IL2CPP 上热更代码的落脚点。</summary>
        Patch,

        /// <summary>配置脚本：纯数据，入口点通常只读不执行逻辑。</summary>
        Config
    }

    /// <summary>分类 ⇄ 清单里的字符串标记。大小写不敏感读入，写出永远是规范形式。</summary>
    public static class ScriptChunkKinds
    {
        /// <summary>规范标记（清单写出用）。</summary>
        public static string ToToken(ScriptChunkKind kind)
        {
            if (kind == ScriptChunkKind.Module)
            {
                return "module";
            }

            if (kind == ScriptChunkKind.Patch)
            {
                return "patch";
            }

            if (kind == ScriptChunkKind.Config)
            {
                return "config";
            }

            throw new ArgumentOutOfRangeException("kind", "ScriptChunkKinds: unknown kind " + (int)kind + ".");
        }

        /// <summary>解析标记；未知标记返回 false（调用方决定怎么把它说成一句人话）。</summary>
        public static bool TryParse(string token, out ScriptChunkKind kind)
        {
            kind = ScriptChunkKind.Module;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (string.Equals(token, "module", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptChunkKind.Module;
                return true;
            }

            if (string.Equals(token, "patch", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptChunkKind.Patch;
                return true;
            }

            if (string.Equals(token, "config", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptChunkKind.Config;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 一段脚本源码（一个 Lua chunk）。装载的最小单位。
    ///
    /// 为什么指纹算在**构造时**而不是装载时：内容一变指纹就变，这是版本差量比对与
    /// 完整性校验共用的同一个值；构造函数是唯一能保证它一定被算过的地方。
    ///
    /// 空文本是合法的（一个空 chunk 就是一段什么都不做的代码），但**空名字不是**：
    /// 名字既是清单里的键，也是 Lua 侧 chunkName，没有名字就没有诊断。
    /// </summary>
    public sealed class ScriptChunk
    {
        /// <summary>逻辑名：清单内唯一，也是运行时侧上报错误时用的 chunkName。</summary>
        public readonly string Name;

        /// <summary>源码。UTF-8 解读后的字符串（来源侧负责严格解码，坏字节不许变成替换符）。</summary>
        public readonly string Text;

        /// <summary>用途分类。</summary>
        public readonly ScriptChunkKind Kind;

        /// <summary>来源标签（清单名 / 文件 / 内存），只用于诊断，不参与任何判定。</summary>
        public readonly string Origin;

        /// <summary>内容指纹：UTF-8 字节的 CRC32。内容变则变。</summary>
        public readonly uint Crc;

        /// <summary>构造一个代码块。名字为空或文本为 null 是程序 bug（调用方拼错了），抛。</summary>
        public ScriptChunk(string name, string text, ScriptChunkKind kind, string origin)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("ScriptChunk: name must not be empty.", "name");
            }

            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            Name = name;
            Text = text;
            Kind = kind;
            Origin = origin;
            Crc = HashOf(text);
        }

        /// <summary>源码长度（字符数）。进度与容量估算用。</summary>
        public int Length { get { return Text.Length; } }

        /// <summary>文本 → 指纹。清单解析期用它只算一遍，装载期再对一次。</summary>
        public static uint HashOf(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Crc32.Compute(Empty);
            }

            return Crc32.Compute(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>指纹的规范写法，让所有报错信息长得一样。</summary>
        public static string ToHex(uint crc)
        {
            return "0x" + crc.ToString("X8");
        }

        /// <summary>诊断用一行。</summary>
        public override string ToString()
        {
            return Name + "[" + ScriptChunkKinds.ToToken(Kind) + "]/" + ToHex(Crc);
        }

        private static readonly byte[] Empty = new byte[0];
    }
}
