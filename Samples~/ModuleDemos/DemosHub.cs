using System.Collections;
using UnityEngine;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 巡演总控：挂到场景里任意空 GameObject 上，按 Play 即可。
    ///
    /// - Start：自动依次上演 21 个模块演示（每个之间停 2 秒方便看 Console）；
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
            Debug.Log("<color=#4FC3F7>=================================================================</color>");
            Debug.Log("<color=#4FC3F7>  Your AI Framework —— 21 个模块演示开始（Console 逐行上演）</color>");
            Debug.Log("<color=#4FC3F7>=================================================================</color>");

            yield return Tour("01/21 模块中心 ModuleCenter —— 一切皆模块，依赖就是数字", DemoCore.ModuleCenter());
            yield return Tour("02/21 事件总线 EventBus —— Publish 同步直发 / Post + Pump 延迟派发", DemoCore.EventBus());
            yield return Tour("03/21 对象池 ObjectPool / GameObjectPool —— 借还配平，失衡即抛", DemoCore.ObjectPools());
            yield return Tour("04/21 存档与设置 SaveSystem / SettingStore —— 原子写 + 键值设置", DemoContent.SaveAndSettings());
            yield return Tour("05/21 流程状态机 ProcedureFlow —— Boot→Menu→Playing", DemoContent.ProcedureFlowDemo());
            yield return Tour("06/21 数据表 DataTable —— 配表读取/点路径/格式化/整表替换", DemoContent.DataTableDemo());
            yield return Tour("07/21 资源 AssetManager —— 统一入口轮询加载，失败是值", DemoContent.AssetsDemo());
            yield return Tour("08/21 本地化 LocalizationStore —— 多语言/回退链/格式化", DemoContent.LocalizationDemo());
            yield return Tour("09/21 UI 面板栈 PanelStack + UIPanelManager —— 层级与独占屏蔽", DemoExperience.UiPanels());
            yield return Tour("10/21 声音 AudioManager —— 分组混音/优先级抢断/实时静音", DemoExperience.Audio());
            yield return Tour("11/21 实体 EntityRegistry —— 小接口自由组合，不强迫继承", DemoExperience.Entities());
            yield return Tour("12/21 网络 NetClient —— 心跳/退避重连/断线排队（演示域名必失败，看失败路径）", DemoServices.Net());
            yield return Tour("13/21 SDK 管道 SdkHub —— 渠道故障自动降级", DemoServices.Sdk());
            yield return Tour("14/21 诊断台 FrameworkDiagnostics —— 计数器/仪表/一键快照", DemoServices.Diagnostics());
            yield return Tour("15/21 热更编排 HotUpdateFlow —— 阶段编排/失败截停/终态纪律", DemoServices.HotUpdate());
            yield return Tour("16/21 AI 模块（可选）—— 无 Key 一切照常，配 Key 即真对话", DemoServices.Ai());
            yield return Tour("17/21 自研资源包 AssetPack —— 清单/版本差量/下载队列/依赖闭包", DemoPack.AssetPack());
            yield return Tour("18/21 自研本地化包 LocalePack —— 语言链/多表共享/CLDR 复数/内联格式化/资源地址", DemoLocale.LocalePack());
            yield return Tour("19/21 自研热更程序集 AssemblyReload —— 清单/拓扑装载/完整性校验/入口点/AOT 白名单", DemoHotAssembly.HotAssemblies());
            yield return Tour("20/21 数值系统 Stats —— 可复现随机/属性表三层求值/泵驱动效果/伤害管道", DemoStats.Stats());
            yield return Tour("21/21 地址目录表 AddressCatalog —— 变体解析/补丁合并/校验/确定性写出", DemoCatalog.AddressCatalogDemo());

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
    }
}
