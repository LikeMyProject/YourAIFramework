using System.Collections;
using UnityEngine;
using YourFramework.UnityRuntime;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 巡演总控：挂到场景里任意空 GameObject 上，按 Play 即可。
    ///
    /// - Start：自动依次上演 23 个模块演示（每个之间停 2 秒方便看 Console）；
    /// - ContextMenu：右键组件名可单独重放任意一个演示。
    ///
    /// 所见即所学：每个 [DemoNN] 日志对应 README 表格里的一行。
    /// </summary>
    public sealed class DemosHub : MonoBehaviour
    {
        private IEnumerator Start()
        {
            DemoSetup.RegisterAll();

            // 等 GameEntry.Start 跑完（全部模块 Init 完成）再开演。
            yield return new WaitForSeconds(1f);

            // 自检：模块的 Init 前置条件是「运行期」的，四道离线门禁都看不见 ——
            // 把它变成一行可见的证据。若某个模块没起来，这行会显示未初始化或状态异常。
            Debug.Log("[DemoTour] 模块自检：注册 " + GameEntry.Instance.Modules.Count
                + " 个，已初始化=" + GameEntry.Instance.Modules.IsInitialized
                + "，流程机 " + DemoSetup.Flow.State + "/" + DemoSetup.Flow.CurrentProcedureName);
            Debug.Log("<color=#4FC3F7>=================================================================</color>");
            Debug.Log("<color=#4FC3F7>  Your AI Framework —— 23 个模块演示开始（Console 逐行上演）</color>");
            Debug.Log("<color=#4FC3F7>=================================================================</color>");

            yield return Tour("01/23 模块中心 ModuleCenter —— 一切皆模块，依赖就是数字", DemoCore.ModuleCenter());
            yield return Tour("02/23 事件总线 EventBus —— Publish 同步直发 / Post + Pump 延迟派发", DemoCore.EventBus());
            yield return Tour("03/23 对象池 ObjectPool / GameObjectPool —— 借还配平，失衡即抛", DemoCore.ObjectPools());
            yield return Tour("04/23 存档与设置 SaveSystem / SettingStore —— 原子写 + 键值设置", DemoContent.SaveAndSettings());
            yield return Tour("05/23 流程状态机 ProcedureFlow —— Boot→Menu→Playing", DemoContent.ProcedureFlowDemo());
            yield return Tour("06/23 数据表 DataTable —— 配表读取/点路径/格式化/整表替换", DemoContent.DataTableDemo());
            yield return Tour("07/23 资源 AssetManager —— 统一入口轮询加载，失败是值", DemoContent.AssetsDemo());
            yield return Tour("08/23 本地化 LocalizationStore —— 多语言/回退链/格式化", DemoContent.LocalizationDemo());
            yield return Tour("09/23 UI 面板栈 PanelStack + UIPanelManager —— 层级与独占屏蔽", DemoExperience.UiPanels());
            yield return Tour("10/23 声音 AudioManager —— 分组混音/优先级抢断/实时静音", DemoExperience.Audio());
            yield return Tour("11/23 实体 EntityRegistry —— 小接口自由组合，不强迫继承", DemoExperience.Entities());
            yield return Tour("12/23 网络 NetClient —— 心跳/退避重连/断线排队（演示域名必失败，看失败路径）", DemoServices.Net());
            yield return Tour("13/23 SDK 管道 SdkHub —— 渠道故障自动降级", DemoServices.Sdk());
            yield return Tour("14/23 诊断台 FrameworkDiagnostics —— 计数器/仪表/一键快照", DemoServices.Diagnostics());
            yield return Tour("15/23 热更编排 HotUpdateFlow —— 阶段编排/失败截停/终态纪律", DemoServices.HotUpdate());
            yield return Tour("16/23 AI 模块（可选）—— 无 Key 一切照常，配 Key 即真对话", DemoServices.Ai());
            yield return Tour("17/23 自研资源包 AssetPack —— 清单/版本差量/下载队列/依赖闭包", DemoPack.AssetPack());
            yield return Tour("18/23 自研本地化包 LocalePack —— 语言链/多表共享/CLDR 复数/内联格式化/资源地址", DemoLocale.LocalePack());
            yield return Tour("19/23 自研热更程序集 AssemblyReload —— 清单/拓扑装载/完整性校验/入口点/AOT 白名单", DemoHotAssembly.HotAssemblies());
            yield return Tour("20/23 数值系统 Stats —— 可复现随机/属性表三层求值/泵驱动效果/伤害管道", DemoStats.Stats());
            yield return Tour("21/23 地址目录表 AddressCatalog —— 变体解析/补丁合并/校验/确定性写出", DemoCatalog.AddressCatalogDemo());
            yield return Tour("22/23 脚本层 Script/XLua —— 清单/装载序/沙箱策略/失败真回滚（IL2CPP 热更代码）", DemoScript.ScriptLayer());
            yield return Tour("23/23 技能 Skills —— 表驱动定义/地址解析/目标选择/冷却与吟唱/数值桥落地", DemoSkill.Skills());

            Debug.Log("<color=#4FC3F7>=================================================================</color>");
            Debug.Log("<color=#4FC3F7>  巡演结束。单独重放：右键 DemosHub 组件 → ContextMenu 菜单。</color>");
            Debug.Log("<color=#4FC3F7>=================================================================</color>");
        }

        private static IEnumerator Tour(string title, IEnumerator demo)
        {
            Debug.Log("<color=#FFD54F>-------- " + title + " --------</color>");
            yield return demo;
            yield return new WaitForSeconds(2f);
        }

        // ---------------- 单独重放入口（右键组件名） ----------------

        [ContextMenu("01 ModuleCenter")]   private void D01() { DemoSetup.RegisterAll(); StartCoroutine(DemoCore.ModuleCenter()); }
        [ContextMenu("02 EventBus")]       private void D02() { DemoSetup.RegisterAll(); StartCoroutine(DemoCore.EventBus()); }
        [ContextMenu("03 ObjectPools")]    private void D03() { DemoSetup.RegisterAll(); StartCoroutine(DemoCore.ObjectPools()); }
        [ContextMenu("04 Save/Settings")]  private void D04() { DemoSetup.RegisterAll(); StartCoroutine(DemoContent.SaveAndSettings()); }
        [ContextMenu("05 ProcedureFlow")]  private void D05() { DemoSetup.RegisterAll(); StartCoroutine(DemoContent.ProcedureFlowDemo()); }
        [ContextMenu("06 DataTable")]      private void D06() { DemoSetup.RegisterAll(); StartCoroutine(DemoContent.DataTableDemo()); }
        [ContextMenu("07 Assets")]         private void D07() { DemoSetup.RegisterAll(); StartCoroutine(DemoContent.AssetsDemo()); }
        [ContextMenu("08 Localization")]   private void D08() { DemoSetup.RegisterAll(); StartCoroutine(DemoContent.LocalizationDemo()); }
        [ContextMenu("09 UI Panels")]      private void D09() { DemoSetup.RegisterAll(); StartCoroutine(DemoExperience.UiPanels()); }
        [ContextMenu("10 Audio")]          private void D10() { DemoSetup.RegisterAll(); StartCoroutine(DemoExperience.Audio()); }
        [ContextMenu("11 Entities")]       private void D11() { DemoSetup.RegisterAll(); StartCoroutine(DemoExperience.Entities()); }
        [ContextMenu("12 Net")]            private void D12() { DemoSetup.RegisterAll(); StartCoroutine(DemoServices.Net()); }
        [ContextMenu("13 SdkHub")]         private void D13() { DemoSetup.RegisterAll(); StartCoroutine(DemoServices.Sdk()); }
        [ContextMenu("14 Diagnostics")]    private void D14() { DemoSetup.RegisterAll(); StartCoroutine(DemoServices.Diagnostics()); }
        [ContextMenu("15 HotUpdate")]      private void D15() { DemoSetup.RegisterAll(); StartCoroutine(DemoServices.HotUpdate()); }
        [ContextMenu("16 AI (optional)")]  private void D16() { DemoSetup.RegisterAll(); StartCoroutine(DemoServices.Ai()); }
        [ContextMenu("17 AssetPack")]     private void D17() { DemoSetup.RegisterAll(); StartCoroutine(DemoPack.AssetPack()); }
        [ContextMenu("18 LocalePack")]     private void D18() { DemoSetup.RegisterAll(); StartCoroutine(DemoLocale.LocalePack()); }
        [ContextMenu("19 HotAssembly")]   private void D19() { DemoSetup.RegisterAll(); StartCoroutine(DemoHotAssembly.HotAssemblies()); }
        [ContextMenu("20 Stats")]           private void D20() { DemoSetup.RegisterAll(); StartCoroutine(DemoStats.Stats()); }
        [ContextMenu("21 AddressCatalog")] private void D21() { DemoSetup.RegisterAll(); StartCoroutine(DemoCatalog.AddressCatalogDemo()); }
        [ContextMenu("22 Script/XLua")]    private void D22() { DemoSetup.RegisterAll(); StartCoroutine(DemoScript.ScriptLayer()); }
        [ContextMenu("23 Skill")]         private void D23() { DemoSetup.RegisterAll(); StartCoroutine(DemoSkill.Skills()); }
    }
}
