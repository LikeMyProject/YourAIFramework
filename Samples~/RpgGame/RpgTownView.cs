using System.Collections.Generic;
using UnityEngine;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 城镇视图：安全区的 3D 地图 —— 石板广场、水井、几栋拼装小屋、树和路灯、
    /// 南城门（缺口 + 门柱）、北边副本传送门，全是基本体 + 颜色，不依赖美术资产。
    /// 操作和野外同一套（<see cref="RpgControl"/>：右键转视角、WASD/QE 走路、
    /// A/D 转身、滚轮拉近拉远），相机同款第三人称跟随。
    ///
    /// 城镇没有竞技场（不刷怪不结算），位置账本就在这个视图里自己记；
    /// 走到泉水 / 传送门 / 城门边上按 F 交互（交互点视图报、宿主接）——
    /// 走路归视图，玩法归宿主。
    /// </summary>
    public sealed class RpgTownView : MonoBehaviour
    {
        private const float TownHalf = 20f;         // 城镇可行走半径
        private const float WalkSpeed = 4f;

        private RpgControl _control;
        private Camera _cam;
        private Transform _root;
        private GameObject _body;
        private float _x;
        private float _z;
        private float _faceYaw;

        // ---- 交互点：走近了出提示，按 F 触发（走路归视图，交互归宿主）----
        private struct InteractPoint { public float X, Z, Radius; public string Id, Label; }
        private readonly List<InteractPoint> _points = new List<InteractPoint>(4);
        private string _currentId;
        private string _currentLabel;

        /// <summary>当前站在哪个交互点边上（不在任何点边上为 null）。</summary>
        public string CurrentInteractionId { get { return _currentId; } }
        public string CurrentInteractionLabel { get { return _currentLabel; } }

        /// <summary>落点控制：从野外/副本回城落复活点（泉水旁），从主菜单进落广场。</summary>
        public void SpawnAt(float x, float z)
        {
            _x = x;
            _z = z;
        }

        public void Init(RpgControl control)
        {
            _control = control;
            // 相机先取好再用 —— MakeTown 里要设天空色（_cam 在后面才赋值时首帧必炸，踩过）
            _cam = Camera.main;
            _cam.orthographic = false;
            _cam.fieldOfView = 60f;
            _cam.farClipPlane = 400f;
            _root = new GameObject("[RpgTownView]").transform;
            MakeTown();
            MakeHero();
        }

        private void LateUpdate()
        {
            float dirX = _control.DirX;
            float dirZ = _control.DirZ;
            float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len > 0.0001f)
            {
                _x += dirX / len * WalkSpeed * Time.deltaTime;
                _z += dirZ / len * WalkSpeed * Time.deltaTime;
                _x = Mathf.Clamp(_x, -TownHalf, TownHalf);
                _z = Mathf.Clamp(_z, -TownHalf, TownHalf);
                _faceYaw = _control.Yaw;            // 角色朝向跟着视角（走哪看哪）
            }

            _body.transform.position = new Vector3(_x, 0.75f, _z);
            Vector3 face = new Vector3(Mathf.Sin(_faceYaw * Mathf.Deg2Rad), 0f, Mathf.Cos(_faceYaw * Mathf.Deg2Rad));
            if (face.sqrMagnitude > 0.0001f)
            {
                _body.transform.rotation = Quaternion.LookRotation(face, Vector3.up);
            }

            FollowCamera();
            UpdateInteraction();
        }

        /// <summary>每帧找最近的交互点（半径内），宿主拿它显示提示、接 F 键。</summary>
        private void UpdateInteraction()
        {
            _currentId = null;
            _currentLabel = null;
            float best = float.MaxValue;
            for (int i = 0; i < _points.Count; i++)
            {
                float dx = _x - _points[i].X;
                float dz = _z - _points[i].Z;
                float d2 = dx * dx + dz * dz;
                if (d2 <= _points[i].Radius * _points[i].Radius && d2 < best)
                {
                    best = d2;
                    _currentId = _points[i].Id;
                    _currentLabel = _points[i].Label;
                }
            }
        }

        /// <summary>第三人称跟随：和野外同一套机位公式（焦点略高，距离来自滚轮）。</summary>
        private void FollowCamera()
        {
            float yawRad = _control.Yaw * Mathf.Deg2Rad;
            float pitchRad = _control.CamPitch * Mathf.Deg2Rad;
            float cosP = Mathf.Cos(pitchRad);
            Vector3 focus = new Vector3(_x, 1.5f, _z);
            Vector3 camPos = focus + new Vector3(-Mathf.Sin(yawRad) * cosP, Mathf.Sin(pitchRad), -Mathf.Cos(yawRad) * cosP) * _control.CamDist;
            _cam.transform.position = camPos;
            _cam.transform.rotation = Quaternion.LookRotation(focus - camPos, Vector3.up);
        }

        // ------------------------------------------------------------------ 拼装城镇

        private void MakeTown()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "TownGround";
            ground.transform.SetParent(_root, false);
            ground.transform.position = new Vector3(0f, -0.15f, 0f);
            ground.transform.localScale = new Vector3(TownHalf * 2f + 10f, 0.3f, TownHalf * 2f + 10f);
            ground.GetComponent<Renderer>().material.color = new Color(0.62f, 0.58f, 0.5f);

            // 中央广场一圈浅色石板（压扁的圆柱），水井立正中间
            MakePart(_root, "Plaza", PrimitiveType.Cylinder,
                new Vector3(0f, 0.02f, 0f), new Vector3(7f, 0.02f, 7f), new Color(0.72f, 0.68f, 0.6f));
            MakeWell(0f, 0f);

            // 六栋小屋围一圈，门都朝向广场
            Vector3[] houseSpots = new Vector3[]
            {
                new Vector3(-12f, 0f, -8f),
                new Vector3(11f, 0f, -10f),
                new Vector3(-13f, 0f, 7f),
                new Vector3(13f, 0f, 6f),
                new Vector3(-4f, 0f, -14f),
                new Vector3(5f, 0f, 13f),
            };
            for (int i = 0; i < houseSpots.Length; i++)
            {
                MakeHouse(houseSpots[i].x, houseSpots[i].z, i);
            }

            // 树和路灯点缀街边
            for (int i = 0; i < 10; i++)
            {
                float ang = i * 36f * Mathf.Deg2Rad + 18f;
                float r = 16.5f;
                MakeTree(Mathf.Sin(ang) * r, Mathf.Cos(ang) * r, 0.9f + (i % 3) * 0.15f);
            }

            for (int i = 0; i < 8; i++)
            {
                float ang = (i + 0.5f) * 45f * Mathf.Deg2Rad;
                float r = 11f;
                MakeLamp(Mathf.Sin(ang) * r, Mathf.Cos(ang) * r);
            }

            // 城墙：低矮的一圈石台，标出"出了城就是野外"；南面 i==12 留缺口做城门
            for (int i = 0; i < 24; i++)
            {
                if (i == 12)
                {
                    continue;                       // 南面缺口 = 城门，交互出去
                }

                float ang = i * Mathf.PI * 2f / 24f;
                MakePart(_root, "TownWall", PrimitiveType.Cube,
                    new Vector3(Mathf.Sin(ang) * (TownHalf + 1.5f), 0.5f, Mathf.Cos(ang) * (TownHalf + 1.5f)),
                    new Vector3(4.6f, 1f, 1.2f), new Color(0.55f, 0.52f, 0.47f));
            }

            MakeGate(0f, -(TownHalf + 1.5f));
            MakePortal(0f, 15f);

            // 交互点注册：泉水（回满存档）、副本传送门、城门（去野外）。
            // 位置跟视觉物件对齐，宿主只认 Id，不认坐标。
            _points.Add(new InteractPoint { X = 0f, Z = 0f, Radius = 3.2f, Id = "well", Label = "恢复泉水（状态回满并保存）" });
            _points.Add(new InteractPoint { X = 0f, Z = 15f, Radius = 3f, Id = "dungeon", Label = "副本传送门（挑战岩石魔王）" });
            _points.Add(new InteractPoint { X = 0f, Z = -18.5f, Radius = 3.6f, Id = "wilds", Label = "城门（前往野外）" });

            // 城外绿地一圈，接野外的草色
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.75f, 0.84f, 0.92f);
            RenderSettings.fogStartDistance = 40f;
            RenderSettings.fogEndDistance = 100f;
            RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.58f);
            _cam.backgroundColor = new Color(0.75f, 0.84f, 0.92f);
        }

        /// <summary>城门：两根门柱 + 横梁 + 一条从广场通到门口的土路。</summary>
        private void MakeGate(float x, float z)
        {
            MakePart(_root, "RoadSouth", PrimitiveType.Cube,
                new Vector3(x, 0.02f, -12f), new Vector3(3.2f, 0.04f, 15f), new Color(0.68f, 0.6f, 0.48f));
            MakePart(_root, "GatePostL", PrimitiveType.Cylinder,
                new Vector3(x - 4.2f, 1.3f, z), new Vector3(0.5f, 1.3f, 0.5f), new Color(0.45f, 0.3f, 0.2f));
            MakePart(_root, "GatePostR", PrimitiveType.Cylinder,
                new Vector3(x + 4.2f, 1.3f, z), new Vector3(0.5f, 1.3f, 0.5f), new Color(0.45f, 0.3f, 0.2f));
            MakePart(_root, "GateLintel", PrimitiveType.Cube,
                new Vector3(x, 2.9f, z), new Vector3(9.4f, 0.5f, 0.8f), new Color(0.5f, 0.34f, 0.22f));
            MakeSign("野外 →", x, 3.8f, z, new Color(1f, 0.9f, 0.55f), 0f);
        }

        /// <summary>副本传送门：石座 + 两片交叉的紫色漩涡环，立在华北角。</summary>
        private void MakePortal(float x, float z)
        {
            MakePart(_root, "PortalBase", PrimitiveType.Cylinder,
                new Vector3(x, 0.15f, z), new Vector3(2.8f, 0.15f, 2.8f), new Color(0.25f, 0.2f, 0.35f));
            GameObject swirlA = MakePart(_root, "PortalSwirlA", PrimitiveType.Cylinder,
                new Vector3(x, 1.5f, z), new Vector3(2.2f, 0.1f, 2.2f), new Color(0.62f, 0.3f, 0.92f));
            swirlA.transform.rotation = Quaternion.Euler(0f, 0f, 90f);      // 立起来，轴向 X
            GameObject swirlB = MakePart(_root, "PortalSwirlB", PrimitiveType.Cylinder,
                new Vector3(x, 1.5f, z), new Vector3(2.2f, 0.1f, 2.2f), new Color(0.35f, 0.4f, 0.95f));
            swirlB.transform.rotation = Quaternion.Euler(90f, 0f, 0f);      // 立起来，轴向 Z
            MakeSign("副本传送门", x, 3.6f, z, new Color(0.85f, 0.7f, 1f), 180f);
        }

        /// <summary>漂浮的 3D 提示牌（动态字体现场造材质，不碰美术资产）。yaw 转牌子朝向。</summary>
        private static void MakeSign(string text, float x, float y, float z, Color color, float yaw)
        {
            GameObject go = new GameObject("Sign_" + text);
            go.transform.position = new Vector3(x, y, z);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            TextMesh tm = go.AddComponent<TextMesh>();
            tm.font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 24);
            tm.fontSize = 24;
            tm.characterSize = 0.2f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            tm.text = text;
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        }

        private void MakeWell(float x, float z)
        {
            MakePart(_root, "WellBase", PrimitiveType.Cylinder,
                new Vector3(x, 0.45f, z), new Vector3(1.6f, 0.45f, 1.6f), new Color(0.55f, 0.52f, 0.48f));
            MakePart(_root, "WellWater", PrimitiveType.Cylinder,
                new Vector3(x, 0.92f, z), new Vector3(1.3f, 0.03f, 1.3f), new Color(0.25f, 0.5f, 0.85f));
            MakePart(_root, "WellPostL", PrimitiveType.Cube,
                new Vector3(x - 0.6f, 1.4f, z), new Vector3(0.12f, 1.6f, 0.12f), new Color(0.4f, 0.29f, 0.18f));
            MakePart(_root, "WellPostR", PrimitiveType.Cube,
                new Vector3(x + 0.6f, 1.4f, z), new Vector3(0.12f, 1.6f, 0.12f), new Color(0.4f, 0.29f, 0.18f));
            MakePart(_root, "WellRoof", PrimitiveType.Cube,
                new Vector3(x, 2.3f, z), new Vector3(1.8f, 0.15f, 1.1f), new Color(0.55f, 0.3f, 0.2f));
        }

        private void MakeHouse(float x, float z, int index)
        {
            float rotY = Mathf.Atan2(-x, -z) * Mathf.Rad2Deg;       // 门朝广场
            GameObject house = new GameObject("House" + index);
            house.transform.SetParent(_root, false);
            house.transform.position = new Vector3(x, 0f, z);
            house.transform.rotation = Quaternion.Euler(0f, rotY, 0f);

            MakePart(house.transform, "Body", PrimitiveType.Cube,
                new Vector3(0f, 1.2f, 0f), new Vector3(3.4f, 2.4f, 2.8f), new Color(0.82f, 0.74f, 0.62f));
            MakePart(house.transform, "RoofL", PrimitiveType.Cube,
                new Vector3(0f, 2.85f, 0.72f), new Vector3(3.7f, 0.18f, 1.85f), new Color(0.62f, 0.3f, 0.22f));
            MakePart(house.transform, "RoofR", PrimitiveType.Cube,
                new Vector3(0f, 2.85f, -0.72f), new Vector3(3.7f, 0.18f, 1.85f), new Color(0.62f, 0.3f, 0.22f));
            MakePart(house.transform, "Door", PrimitiveType.Cube,
                new Vector3(0f, 0.65f, 1.42f), new Vector3(0.7f, 1.3f, 0.08f), new Color(0.4f, 0.28f, 0.17f));
            MakePart(house.transform, "WindowL", PrimitiveType.Cube,
                new Vector3(-1f, 1.4f, 1.42f), new Vector3(0.5f, 0.5f, 0.06f), new Color(0.65f, 0.82f, 0.9f));
            MakePart(house.transform, "WindowR", PrimitiveType.Cube,
                new Vector3(1f, 1.4f, 1.42f), new Vector3(0.5f, 0.5f, 0.06f), new Color(0.65f, 0.82f, 0.9f));
        }

        private void MakeTree(float x, float z, float scale)
        {
            MakePart(_root, "TreeTrunk", PrimitiveType.Cylinder,
                new Vector3(x, 0.7f * scale, z), new Vector3(0.32f * scale, 1.4f * scale, 0.32f * scale),
                new Color(0.4f, 0.29f, 0.18f));
            MakePart(_root, "TreeCrown", PrimitiveType.Sphere,
                new Vector3(x, 1.9f * scale, z), new Vector3(1.8f * scale, 1.6f * scale, 1.8f * scale),
                new Color(0.24f, 0.5f, 0.22f));
        }

        private void MakeLamp(float x, float z)
        {
            MakePart(_root, "LampPole", PrimitiveType.Cylinder,
                new Vector3(x, 1f, z), new Vector3(0.09f, 2f, 0.09f), new Color(0.3f, 0.3f, 0.33f));
            MakePart(_root, "LampBulb", PrimitiveType.Sphere,
                new Vector3(x, 2.15f, z), Vector3.one * 0.28f, new Color(1f, 0.88f, 0.5f));
        }

        private void MakeHero()
        {
            _body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _body.name = "Hero";
            _body.transform.SetParent(_root, false);
            _body.transform.position = new Vector3(0f, 0.75f, 3f);
            _body.GetComponent<Renderer>().material.color = new Color(0.25f, 0.55f, 0.95f);
            MakePart(_body.transform, "Nose", PrimitiveType.Cube,
                new Vector3(0f, 0.18f, 0.42f), new Vector3(0.16f, 0.16f, 0.3f), new Color(0.15f, 0.35f, 0.7f));
            _x = 0f;
            _z = 3f;
        }

        /// <summary>拼一个零件：基本体 + 局部位置 + 缩放 + 颜色（父物体在原点时局部=世界）。</summary>
        private static GameObject MakePart(Transform parent, string name, PrimitiveType type,
            Vector3 position, Vector3 scale, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().material.color = color;
            return part;
        }
    }
}
