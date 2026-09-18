namespace YourFramework.Assets.Pack
{
    /// <summary>
    /// CRC32（IEEE 802.3，反射多项式 0xEDB88320）—— 下载落地后的完整性校验值。
    ///
    /// 为什么不用 MD5/SHA：这里的对手是"传输截断 / 文件写坏"，不是恶意篡改；
    /// CRC32 单文件几个纳秒级字节、可在下载流里边收边算，校验成本远低于一次
    /// 重新下载。内容指纹（防篡改、跨版本比对）用 Hash 字段走各自后端自己的
    /// 强哈希，两者的职责不混。
    ///
    /// 分块用法：Begin → Update* → Finish。三者必须配套，中途混用 Compute 的结果
    /// 不是"近似正确"，而是完全错的数。
    /// </summary>
    public static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;
        private static readonly uint[] Table = BuildTable();

        /// <summary>Initial accumulator for a streamed computation.</summary>
        public static uint Begin()
        {
            return 0xFFFFFFFFu;
        }

        /// <summary>Folds a chunk into a running accumulator.</summary>
        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            if (data == null)
            {
                return crc;
            }

            uint running = crc;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                running = (running >> 8) ^ Table[(running ^ data[i]) & 0xFF];
            }

            return running;
        }

        /// <summary>Closes a streamed computation (inverts the accumulator).</summary>
        public static uint Finish(uint crc)
        {
            return ~crc;
        }

        /// <summary>One-shot CRC32 over a whole buffer.</summary>
        public static uint Compute(byte[] data)
        {
            return data == null ? 0u : Compute(data, 0, data.Length);
        }

        /// <summary>One-shot CRC32 over a slice.</summary>
        public static uint Compute(byte[] data, int offset, int count)
        {
            return Finish(Update(Begin(), data, offset, count));
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
