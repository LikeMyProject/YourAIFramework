using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YourAI.Agent;
using YourAI.Presentation;

namespace YourAI.EditorTools
{
    /// <summary>
    /// The settings and prompt window: what is configured, what will be sent.
    ///
    /// Two panels, one purpose -- "why does my character behave like this":
    ///
    /// - Diagnostics: AgentDiagnostics.Inspect over the runtime's primary agent.
    ///   Severity-honest findings (a missing key is a warning, missing wiring an
    ///   error), each with a sentence that names what to fix.
    ///
    /// - Prompt preview: PromptPreview.Render over AiAgent.PreviewMessages -- the
    ///   same builder the request uses, so this is exactly what the model will
    ///   see, persona and context and history and the typed line, with token
    ///   estimates, before any tokens are spent.
    ///
    /// Everything the window shows comes from the engine-free Presentation
    /// assembly, which is where the actual logic lives and is tested. This class
    /// is a renderer: if it grows past ~300 lines, the next rule belongs in
    /// AgentDiagnostics, not here.
    ///
    /// Open it from Window / YourAI / Agent Settings.
    /// </summary>
    public sealed class AiSettingsWindow : EditorWindow
    {
        private AiAgent _agent;
        private string _userLine = "多少钱？";
        private Vector2 _diagnosticsScroll;
        private Vector2 _previewScroll;
        private string _previewText;

        private List<Diagnostic> _findings;

        [MenuItem("Window/YourAI/Agent Settings")]
        public static void Open()
        {
            AiSettingsWindow window = GetWindow<AiSettingsWindow>("YourAI Agent");
            window.minSize = new Vector2(480f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshAgent();
        }

        private void RefreshAgent()
        {
            _agent = null;

            YourAI.UnityRuntime.AiRuntimeBehaviour runtime =
                YourAI.UnityRuntime.AiRuntimeBehaviour.Instance;
            if (runtime == null)
            {
                // FindAnyObjectByType, not FindObjectOfType: Unity 6 deprecated the
                // latter, and the compile guard is what caught that.
                runtime = Object.FindAnyObjectByType<YourAI.UnityRuntime.AiRuntimeBehaviour>();
            }

            if (runtime != null)
            {
                _agent = runtime.PrimaryAgent;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("从场景读取运行时", GUILayout.Width(150f)))
            {
                RefreshAgent();
                _findings = null;
                _previewText = null;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                _agent != null ? "agent: " + _agent.Name : "场景中没有 AiRuntimeBehaviour / agent",
                _agent != null ? EditorStyles.boldLabel : EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("诊断", EditorStyles.boldLabel);
            DrawDiagnostics();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Prompt 预览（不会发送任何请求）", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _userLine = EditorGUILayout.TextField("用户台词", _userLine);
            if (GUILayout.Button("生成预览", GUILayout.Width(90f)))
            {
                GeneratePreview();
            }
            EditorGUILayout.EndHorizontal();

            DrawPreview();
            EditorGUILayout.EndVertical();
        }

        // ----------------------------------------------------------------- panels

        private void DrawDiagnostics()
        {
            if (_agent == null)
            {
                EditorGUILayout.HelpBox(
                    "没有可检查的 agent。先在场景里创建一个 AiRuntimeBehaviour 并 Setup 一个 agent。",
                    MessageType.Info);
                return;
            }

            if (_findings == null)
            {
                _findings = AgentDiagnostics.Inspect(_agent);
            }

            _diagnosticsScroll = EditorGUILayout.BeginScrollView(_diagnosticsScroll, GUILayout.MinHeight(120f));

            int errors = AgentDiagnostics.Count(_findings, DiagnosticSeverity.Error);
            int warnings = AgentDiagnostics.Count(_findings, DiagnosticSeverity.Warning);

            EditorGUILayout.LabelField(
                string.Format("{0} 项发现：{1} 错误，{2} 警告", _findings.Count, errors, warnings),
                EditorStyles.miniBoldLabel);

            for (int i = 0; i < _findings.Count; i++)
            {
                Diagnostic finding = _findings[i];

                MessageType icon = MessageType.None;
                Color color = GUI.color;
                switch (finding.Severity)
                {
                    case DiagnosticSeverity.Error:
                        icon = MessageType.Error;
                        GUI.color = new Color(1f, 0.75f, 0.72f);
                        break;
                    case DiagnosticSeverity.Warning:
                        icon = MessageType.Warning;
                        GUI.color = new Color(1f, 0.95f, 0.75f);
                        break;
                    default:
                        icon = MessageType.Info;
                        break;
                }

                EditorGUILayout.HelpBox(finding.Message, icon);
                GUI.color = color;
            }

            EditorGUILayout.EndScrollView();
        }

        private void GeneratePreview()
        {
            _previewText = null;
            if (_agent == null)
            {
                return;
            }

            // Same builder the request uses -- the preview cannot drift from what
            // is sent, because it IS what is sent.
            _previewText = PromptPreview.Render(_agent.PreviewMessages(null, _userLine));
        }

        private void DrawPreview()
        {
            if (_agent == null)
            {
                return;
            }

            if (_previewText == null)
            {
                EditorGUILayout.HelpBox("点“生成预览”查看下一次请求的完整内容。", MessageType.None);
                return;
            }

            _previewScroll = EditorGUILayout.BeginScrollView(_previewScroll, GUILayout.MinHeight(160f));
            EditorGUILayout.TextArea(_previewText, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
    }
}
