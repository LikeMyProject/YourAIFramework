#if YOURAI_UNITY_LOCALIZATION
using System;
using UnityEngine.Localization.Settings;
using YourFramework.Localization;

namespace YourFramework.UnityRuntime.Localization
{
    /// <summary>
    /// Unity Localization 桥（引入不自研）：把编辑器资产表里配置好的 Unity
    /// Localization 内容挂到 LocalizationStore 的源插槽上。
    ///
    /// 编译边界：整文件在 YOURAI_UNITY_LOCALIZATION 符号内，由
    /// YourFramework.UnityRuntime.asmdef 的 versionDefines 在宿主工程装了
    /// com.unity.localization（1.5+）时自动定义 —— 没装包编译为空，装了包
    /// API 变更立刻浮出为编译错误（与 YooAssetProvider 同一纪律）。
    ///
    /// 语义映射：
    /// - 表名固定由构造传入（通常一个主表），key 原样透传；
    /// - 语言参数与 Unity Localization 的全局 Locale 选择各自独立：仅当请求
    ///   语言与全局 Locale 代码一致时才认领（避免两套语言状态互相拉扯）；
    /// - 同步取词走 StringDatabase 的同步重载，表未预载/未命中一律返回
    ///   false，不阻塞、不抛异常 —— 存储层继续走自己的链。
    /// </summary>
    public sealed class UnityLocalizationSource : ILocalizationSource
    {
        private readonly string _tableKey;

        /// <summary>
        /// Builds the source over a Unity Localization table key (e.g. the
        /// table collection name). Fail fast on null: a bridge without a table
        /// is a wiring bug.
        /// </summary>
        public UnityLocalizationSource(string tableKey)
        {
            if (string.IsNullOrEmpty(tableKey))
            {
                throw new ArgumentException(
                    "UnityLocalizationSource: table key must not be null or empty.", "tableKey");
            }

            _tableKey = tableKey;
        }

        /// <summary>Source name (diagnostics).</summary>
        public string Name
        {
            get { return "UnityLocalization:" + _tableKey; }
        }

        /// <summary>Sync lookup; claims only when the selected locale matches the request.</summary>
        public bool TryGet(string language, string key, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            try
            {
                string selected = LocalizationSettings.SelectedLocale != null
                    ? LocalizationSettings.SelectedLocale.Identifier.Code
                    : null;
                if (selected == null || !string.Equals(selected, language, StringComparison.Ordinal))
                {
                    return false; // different language state: not ours to answer
                }

                string value = LocalizationSettings.StringDatabase.GetLocalizedString(_tableKey, key);
                if (string.IsNullOrEmpty(value))
                {
                    return false; // missing entry or table not preloaded
                }

                text = value;
                return true;
            }
            catch (Exception)
            {
                // Localization runtime trouble (tables not initialized yet, etc.)
                // is a miss from this source's point of view, never a crash.
                return false;
            }
        }
    }
}
#endif
