using System;
using System.Collections.Generic;
using System.Globalization;
using YourAI.Core.Json;

namespace YourFramework.Data
{
    /// <summary>
    /// 数据表：JSON 行数组 → 主键索引 → 类型化单元格访问。
    ///
    /// 蓝图里"数据表管理"的运行时半边（策划配 → 工具导出 → 运行时读），Excel→JSON
    /// 的导出工具侧由内容管线负责，本类只认约定格式：
    ///
    ///     [ {"id":"sword_001","name":"铁剑","price":120,"sellable":true}, ... ]
    ///
    /// 出错时的处理（刻意与运行期故障不同）：**表错误一律 fail-fast**——数据表是构建期
    /// 产物、启动时加载，坏行在这里炸出来好过运行中静默查空；这符合 ModuleCenter
    /// 注册规则的同一判断。运行期的 I/O 失败仍走"失败也是值"，由加载器层负责。
    ///
    /// 数值热更：Load 进 DataTableSet 可整表替换（同表名覆盖），热更管线落地后
    /// 直接对接。线程：加载后只读，可多线程并发查。
    /// </summary>
    public sealed class DataTable
    {
        private readonly Dictionary<string, JsonValue> _rows =
            new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        private readonly List<string> _keysInOrder = new List<string>();
        private readonly string _keyField;

        private DataTable(string keyField)
        {
            _keyField = keyField;
        }

        /// <summary>表名惯例上等于文件名；本类不管名字，集合层管。</summary>
        public string KeyField { get { return _keyField; } }

        /// <summary>Row count.</summary>
        public int RowCount { get { return _rows.Count; } }

        /// <summary>
        /// Parses a JSON array of row objects and indexes them by
        /// <paramref name="keyField"/>. Invalid JSON, non-object rows, or missing
        /// key fields throw immediately (build-time artifact, fail-fast).
        /// </summary>
        public static DataTable FromJson(string json, string keyField = "id")
        {
            JsonValue parsed;
            try
            {
                parsed = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                // Uniform fail-fast surface: every table error is an
                // ArgumentException, whatever the parser threw underneath.
                throw new ArgumentException("DataTable: invalid JSON: " + ex.Message, ex);
            }

            return FromValue(parsed, keyField);
        }

        /// <summary>Same contract as <see cref="FromJson"/>, from an already-parsed array.</summary>
        public static DataTable FromValue(JsonValue rows, string keyField = "id")
        {
            if (keyField == null || keyField.Length == 0)
            {
                throw new ArgumentException("DataTable: key field must not be empty.", "keyField");
            }

            if (rows == null || rows.Kind != JsonKind.Array)
            {
                throw new ArgumentException("DataTable: root must be a JSON array of row objects.");
            }

            DataTable table = new DataTable(keyField);
            for (int i = 0; i < rows.Items.Count; i++)
            {
                JsonValue row = rows.Items[i];
                if (row.Kind != JsonKind.Object)
                {
                    throw new ArgumentException(
                        "DataTable: row " + i + " is not a JSON object.");
                }

                JsonValue keyValue = row[keyField];
                if (keyValue == null || keyValue.Kind == JsonKind.Null)
                {
                    throw new ArgumentException(
                        "DataTable: row " + i + " is missing the key field '" + keyField + "'.");
                }

                string key = keyValue.AsString(null);
                if (key == null || key.Length == 0)
                {
                    throw new ArgumentException(
                        "DataTable: row " + i + " has an empty '" + keyField + "'.");
                }

                if (table._rows.ContainsKey(key))
                {
                    throw new ArgumentException(
                        "DataTable: duplicate key '" + key + "' (rows are 0-based).");
                }

                table._rows.Add(key, row);
                table._keysInOrder.Add(key);
            }

            return table;
        }

        /// <summary>Row keys in file order (stable for deterministic iteration).</summary>
        public List<string> Keys { get { return new List<string>(_keysInOrder); } }

        /// <summary>All rows in file order. Read-only usage: rows are live JsonValues.</summary>
        public List<JsonValue> AllRows
        {
            get
            {
                List<JsonValue> all = new List<JsonValue>(_keysInOrder.Count);
                for (int i = 0; i < _keysInOrder.Count; i++)
                {
                    all.Add(_rows[_keysInOrder[i]]);
                }

                return all;
            }
        }

        /// <summary>Whether a row with this key exists.</summary>
        public bool Has(string key)
        {
            return key != null && _rows.ContainsKey(key);
        }

        /// <summary>Row by key, or null. Missing keys are normal business, not errors.</summary>
        public JsonValue GetRow(string key)
        {
            JsonValue row;
            return key != null && _rows.TryGetValue(key, out row) ? row : null;
        }

        /// <summary>Cell by dotted path from the row root, or JsonValue.Null when absent.</summary>
        public JsonValue GetCell(string key, string fieldPath)
        {
            JsonValue row = GetRow(key);
            if (row == null || fieldPath == null)
            {
                return JsonValue.Null;
            }

            JsonValue cell = row.Path(fieldPath);
            return cell ?? JsonValue.Null;
        }

        public string GetString(string key, string fieldPath, string fallback = null)
        {
            return GetCell(key, fieldPath).AsString(fallback);
        }

        public int GetInt(string key, string fieldPath, int fallback = 0)
        {
            return GetCell(key, fieldPath).AsInt(fallback);
        }

        public float GetFloat(string key, string fieldPath, float fallback = 0f)
        {
            return (float)GetCell(key, fieldPath).AsDouble(fallback);
        }

        public bool GetBool(string key, string fieldPath, bool fallback = false)
        {
            return GetCell(key, fieldPath).AsBool(fallback);
        }

        /// <summary>
        /// Formatted cell: {field} placeholders replaced from the row (本地化与
        /// 描述文本的惯例). Unknown placeholders stay as-is.
        /// </summary>
        public string Format(string key, string fieldPath)
        {
            string template = GetString(key, fieldPath);
            if (template == null)
            {
                return null;
            }

            JsonValue row = GetRow(key);
            string result = template;
            foreach (KeyValuePair<string, JsonValue> member in row.Members)
            {
                string placeholder = "{" + member.Key + "}";
                if (result.IndexOf(placeholder, StringComparison.Ordinal) >= 0)
                {
                    string replacement;
                    if (member.Value.Kind == JsonKind.String)
                    {
                        replacement = member.Value.StringValue;
                    }
                    else
                    {
                        replacement = member.Value.ToJson();
                    }

                    result = result.Replace(placeholder, replacement);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 多表集合：按表名管理（配置/物品/任务……）。同表名再次 Load = 整表替换，
    /// 这是数值热更的落点。线程：替换与查询并发由宿主约束（启动后只读最稳）。
    /// </summary>
    public sealed class DataTableSet
    {
        private readonly Dictionary<string, DataTable> _tables =
            new Dictionary<string, DataTable>(StringComparer.Ordinal);

        /// <summary>Registered table count.</summary>
        public int Count { get { return _tables.Count; } }

        /// <summary>Loads (or replaces) a table under a name. Replacement is the hot-update path.</summary>
        public void Load(string name, DataTable table)
        {
            if (name == null || name.Length == 0)
            {
                throw new ArgumentException("DataTableSet.Load: table name must not be empty.", "name");
            }

            if (table == null)
            {
                throw new ArgumentNullException("table");
            }

            _tables[name] = table;
        }

        /// <summary>Convenience: parses and loads in one step.</summary>
        public DataTable LoadJson(string name, string json, string keyField = "id")
        {
            DataTable table = DataTable.FromJson(json, keyField);
            Load(name, table);
            return table;
        }

        /// <summary>The table, or null when absent (callers decide whether that is fatal).</summary>
        public DataTable Get(string name)
        {
            DataTable table;
            return name != null && _tables.TryGetValue(name, out table) ? table : null;
        }

        /// <summary>Whether a table with this name is loaded.</summary>
        public bool Has(string name)
        {
            return name != null && _tables.ContainsKey(name);
        }

        /// <summary>Drops every table.</summary>
        public void Clear()
        {
            _tables.Clear();
        }
    }
}
