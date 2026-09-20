using UnityEngine;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 输入：传统 RPG 的操作模式（诛仙 / 魔兽那一套）——
    /// - W/S 前进后退，Q/E 左右平移（方向跟视角走）；
    /// - A/D 转身（角色面朝变向）；
    /// - 按住鼠标右键拖动转视角（水平转朝向、垂直调俯仰），滚轮拉近拉远；
    /// - 空格或 J 持续攻击（冷却在竞技场里限速），1/2/3 放技能，I 开合背包，回车开局/再战。
    ///
    /// 面朝（Facing）与走位分离：面朝跟着视角走，喂给竞技场当瞄准方向；
    /// 移动是"面朝 + 平移"的合成向量，竞技场里归一。
    /// </summary>
    public sealed class RpgControl : MonoBehaviour
    {
        private System.Func<bool> _isPlaying;
        private System.Func<bool> _canStart;
        private System.Func<bool> _canRestart;
        private System.Action _start;
        private System.Action _toggleInventory;

        private const float TurnSpeed = 120f;       // A/D 转身：每秒转过的角度
        private const float LookSpeed = 2.4f;       // 右键拖动：每像素转过的角度
        private const float PitchSpeed = 1.4f;
        private const float PitchMin = 8f;
        private const float PitchMax = 55f;
        private const float DistMin = 3f;
        private const float DistMax = 9f;

        /// <summary>角色面朝角（度，0 = 朝 +Z，往右转为正）。相机和瞄准都认它。</summary>
        public float Yaw { get; private set; }

        /// <summary>相机俯仰角（度）与距离（米），滚轮/右键上下调。</summary>
        public float CamPitch { get; private set; }
        public float CamDist { get; private set; }

        /// <summary>面朝的单位向量（瞄准用，喂给竞技场 SetFacing）。</summary>
        public float FacingX { get { return Mathf.Sin(Yaw * Mathf.Deg2Rad); } }
        public float FacingZ { get { return Mathf.Cos(Yaw * Mathf.Deg2Rad); } }

        /// <summary>本帧移动意图（世界方向，未归一；竞技场里归一）。</summary>
        public float DirX { get; private set; }
        public float DirZ { get; private set; }

        /// <summary>是否按住攻击键（按住连打，节奏由攻击冷却管）。</summary>
        public bool AttackHeld { get; private set; }

        /// <summary>本帧请求的技能栏序号（1/2/3 键），-1 = 无请求。宿主读完即清。</summary>
        public int SkillRequest { get; private set; }

        private bool _interact;

        /// <summary>本帧按了交互键（F）。宿主读完即清，不读不累积（每帧开头先清零）。</summary>
        public bool ConsumeInteract()
        {
            bool pressed = _interact;
            _interact = false;
            return pressed;
        }

        public void Init(System.Func<bool> isPlaying, System.Func<bool> canStart, System.Func<bool> canRestart,
            System.Action start, System.Action toggleInventory)
        {
            _isPlaying = isPlaying;
            _canStart = canStart;
            _canRestart = canRestart;
            _start = start;
            _toggleInventory = toggleInventory;
            CamPitch = 24f;
            CamDist = 5.5f;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Return) && ((_canStart != null && _canStart()) || (_canRestart != null && _canRestart())))
            {
                _start();
            }

            if (Input.GetKeyDown(KeyCode.I) && _toggleInventory != null)
            {
                _toggleInventory();
            }

            DirX = 0f;
            DirZ = 0f;
            AttackHeld = false;
            SkillRequest = -1;
            _interact = false;
            bool playing = _isPlaying != null && _isPlaying();

            // 交互键只在游玩中登记（城镇一直算游玩中；战斗里阵亡时不许交互）
            if (playing && Input.GetKeyDown(KeyCode.F))
            {
                _interact = true;
            }

            // 视角操作只在游玩中生效；面板场景里鼠标留给界面
            if (playing)
            {
                UpdateLook();
            }

            if (!playing)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) { SkillRequest = 0; }
            else if (Input.GetKeyDown(KeyCode.Alpha2)) { SkillRequest = 1; }
            else if (Input.GetKeyDown(KeyCode.Alpha3)) { SkillRequest = 2; }

            // A/D 转身（传统键位：转身归键盘，视角归鼠标右键）
            if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) { Yaw -= TurnSpeed * Time.deltaTime; }
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) { Yaw += TurnSpeed * Time.deltaTime; }

            // W/S 前后 + Q/E 平移，方向按面朝合成（sin/cos 手写，不用 Transform，纯数据）
            float rad = Yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);
            float fwd = 0f;
            float strafe = 0f;
            if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) { fwd += 1f; }
            if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) { fwd -= 1f; }
            if (Input.GetKey(KeyCode.Q)) { strafe -= 1f; }
            if (Input.GetKey(KeyCode.E)) { strafe += 1f; }
            DirX = sin * fwd + cos * strafe;
            DirZ = cos * fwd - sin * strafe;

            AttackHeld = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.J);
        }

        /// <summary>右键拖动转视角 + 滚轮拉远拉近（诛仙/魔兽的手感）。</summary>
        private void UpdateLook()
        {
            if (Input.GetMouseButton(1))
            {
                Yaw += Input.GetAxis("Mouse X") * LookSpeed * 10f;
                CamPitch = Mathf.Clamp(CamPitch + Input.GetAxis("Mouse Y") * PitchSpeed, PitchMin, PitchMax);
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f)
            {
                CamDist = Mathf.Clamp(CamDist - scroll * 6f, DistMin, DistMax);
            }
        }
    }
}
