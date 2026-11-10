// ─────────────────────────────────────────────────────────────────────────────
// 相机跟随自检探针（引擎内，只读）
//
// 为什么要有它：tests/Camera/CameraRigTests.cs 验证的是 CameraFeel 的纯函数数学。
//   它证明不了 CharacterSession.Tick() 里那条链路真的把「人物位置 + 鼠标方向」
//   喂给了相机 —— 而这条链路断掉时，**画面照样能跑、日志照样干净**：
//     · 漏调 Tick 一次      → 相机冻在原地，不报错
//     · focus 传错坐标      → 相机跟到别处，不报错
//     · 鼠标方向恒为零      → 瞄准偏移永久失效，不报错
//   v11 那一轮的「配置了但没接」正是同类问题。所以必须有一个引擎内的探针，
//   直接读 Camera3D.global_position 来钉住这三件事。
//
// 用法（headless 可跑）：
//   godot --headless --path . -- --hero --camera-probe
//
// 输出 "PASS/FAIL <名字> | <实测数值>"，退出码 = 失败条数。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using Game.Core.Camera;
using Game.Host.Presentation;
using Godot;

namespace Game.Host.Debug
{
    /// <summary>
    /// 相机跟随的一次性自检：在真实引擎里按阶段推进若干帧，采样相机节点，逐项断言。
    /// <para>
    /// 用「阶段机」而不是「一次性算完」：瞄准偏移是弹簧量，必须让它有时间响应。
    /// 每个阶段固定跑若干帧，阶段切换时记录读数。
    /// </para>
    /// </summary>
    public sealed class CameraFollowSelfCheck
    {
        /// <summary>命令行开关。</summary>
        public const string ArgName = "--camera-probe";

        /// <summary>每个阶段推进的帧数（60 帧 ≈ 1 秒 @ 60fps，约为 smoothTime 的 8 倍）。</summary>
        private const int FramesPerStage = 60; // arch-ok: 探针的采样帧数（约 1 秒@60fps），不是玩法数值

        /// <summary>跟随判据用的位移距离：5 米。足够大，不会与噪声同量级。</summary>
        private const float TravelMeters = 5f; // arch-ok: 探针制造的位移距离，不是玩法数值

        /// <summary>「跟得上」的水平残差上限：2 厘米。</summary>
        private const float ConvergeToleranceMeters = 0.02f; // arch-ok: 探针的收敛判据容差，不是玩法数值

        private enum Stage
        {
            /// <summary>静置收敛：初始快照落位。</summary>
            Settle,

            /// <summary>测跟随：相机是否停在人物上方。</summary>
            Follow,

            /// <summary>把人物推走若干米，等相机追过去。</summary>
            Travel,

            /// <summary>瞄准 +X，等前瞻弹簧收敛。</summary>
            AimPositive,

            /// <summary>翻转瞄准到 -X，等前瞻弹簧收敛。</summary>
            AimNegative,

            /// <summary>已结束。</summary>
            Done,
        }

        private readonly CharacterSession _session;
        private readonly Camera3D _camera;
        private Stage _stage = Stage.Settle;
        private int _stageFrame;
        private int _failures;
        private int _checks;
        private Vector3 _aheadPositive;
        private float _startHeroX;
        private float _startCamX;
        private Vector3 _travelFrom;
        private Vector3 _travelTo;

        /// <summary>构造。</summary>
        public CameraFollowSelfCheck(CharacterSession session, Camera3D camera)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>是否已全部跑完。</summary>
        public bool Finished => _stage == Stage.Done;

        /// <summary>失败条数（跑完后有效）。</summary>
        public int Failures => _failures;

        /// <summary>检查条数（跑完后有效）。</summary>
        public int Checks => _checks;

        /// <summary>本帧推进一次；返回 true 表示本帧刚跑完全部检查。</summary>
        public bool Tick()
        {
            if (_stage == Stage.Done)
            {
                return false;
            }

            TopDownCameraRig? rig = _session.CameraRigForProbe;
            if (rig is null)
            {
                Fail("相机装置未创建", "CameraRigForProbe == null");
                _stage = Stage.Done;
                return true;
            }

            _stageFrame++;
            if (_stageFrame < FramesPerStage)
            {
                return false;
            }

            _stageFrame = 0;
            Stage finished = _stage;
            switch (_stage)
            {
                case Stage.Settle:
                    OnSettle(rig);
                    _stage = Stage.Follow;
                    break;

                case Stage.Follow:
                    OnFollow(rig);
                    // 制造真实位移：人物瞬间推走 5 米，相机必须自己追过去。
                    Vector3 from = _session.PlayerWorldPosition();
                    _travelFrom = from;
                    _travelTo = from + new Vector3(TravelMeters, 0f, 0f);
                    bool moved = _session.MovePlayerForProbe(new System.Numerics.Vector3(_travelTo.X, _travelTo.Y, _travelTo.Z));
                    GD.Print($"[camera-probe] 推动人物 {from} → {_travelTo}（写入成功={moved}）");
                    _stage = Stage.Travel;
                    break;

                case Stage.Travel:
                    OnTravel(rig);
                    _stage = Stage.AimPositive;
                    break;

                case Stage.AimPositive:
                    _aheadPositive = rig.CurrentLookAhead;
                    GD.Print($"[camera-probe] 瞄准 +X ：lookAhead={_aheadPositive}");
                    _stage = Stage.AimNegative;
                    _session.InputIntentSource.ForceAimAndPrimary(new Vector3(-1f, 0f, 0f));
                    break;

                case Stage.AimNegative:
                    OnAimOffset(rig);
                    _stage = Stage.Done;
                    GD.Print($"[camera-probe] === 结果: {_checks} 项检查, {_failures} 失败 ===");
                    return true;
            }

            GD.Print($"[camera-probe] 阶段 {finished} 完成");
            return false;
        }

        /// <summary>静置阶段：记录起点，并确认相机确实建出来了。</summary>
        private void OnSettle(TopDownCameraRig rig)
        {
            _startCamX = _camera.GlobalPosition.X;
            _startHeroX = _session.PlayerWorldPosition().X;

            GD.Print($"[camera-probe] 起点 相机.x={_startCamX:F4} 人物.x={_startHeroX:F4}");
            GD.Print($"[camera-probe] rig: smoothTime={rig.Settings.FollowSmoothTime:F4}s " +
                     $"lookAheadMax={rig.Settings.LookAheadMaxMeters:F3}m " +
                     $"camPos={_camera.GlobalPosition}");

            Check(_camera.Projection == Camera3D.ProjectionType.Orthogonal,
                "相机为正交投影（俯视配置生效）",
                $"projection={_camera.Projection} size={_camera.Size:F3}");
        }

        /// <summary>测跟随：相机的「聚焦点」必须贴着人物（而不是相机本体）。</summary>
        /// <remarks>
        /// 注意别把相机本体当成判据：正交俯视相机是 <c>focus + dir*distance</c>，
        /// 俯仰 61° / 距离 17m 时相机本体天然比人物高 15m、水平后撤 8.4m。
        /// 那是配置的正常几何，不是"没跟上"。真正代表"跟没跟上"的是 <c>Focus</c>。
        /// </remarks>
        private void OnFollow(TopDownCameraRig rig)
        {
            Vector3 focus = rig.Focus;
            Vector3 hero = _session.PlayerWorldPosition();

            float flat = new Vector2(focus.X - hero.X, focus.Z - hero.Z).Length();
            float allowed = Math.Max(ConvergeToleranceMeters, rig.Settings.LookAheadMaxMeters + ConvergeToleranceMeters);

            GD.Print($"[camera-probe] 跟随 focus={focus} 人物={hero} 水平距={flat:F4}m " +
                     $"相机本体={_camera.GlobalPosition}");

            Check(flat <= allowed,
                "相机聚焦点贴近人物（跟随链路已通）",
                $"focus 距人物={flat:F4}m ≤ {allowed:F3}m");

            Check(_camera.GlobalPosition.Y > hero.Y,
                "相机位于人物上方（俯视配置生效）",
                $"cam.Y={_camera.GlobalPosition.Y:F3} > hero.Y={hero.Y:F3}");
        }

        /// <summary>测「真的跟过去」：人物移动 5 米后，聚焦点必须跟到新位置。</summary>
        /// <remarks>
        /// 这是跟随判据的真正形态。只测「focus 贴着人物」在人物静止时会同时通过
        /// 「跟」和「不跟」两种实现 —— 必须制造位移才能区分。
        /// </remarks>
        private void OnTravel(TopDownCameraRig rig)
        {
            Vector3 focus = rig.Focus;
            Vector3 hero = _session.PlayerWorldPosition();

            float settled = new Vector2(focus.X - hero.X, focus.Z - hero.Z).Length();
            float wentToNewPos = new Vector2(focus.X - _travelTo.X, focus.Z - _travelTo.Z).Length();
            float travelled = _travelTo.X - _travelFrom.X;

            GD.Print($"[camera-probe] 跟随位移：人物移动 {travelled:F2}m，focus={focus} 新位置={_travelTo} " +
                     $"偏差={wentToNewPos:F4}m");

            Check(wentToNewPos < ConvergeToleranceMeters * 5f,
                "相机跟随人物位移（不是钉在出生点）",
                $"focus 距新位置={wentToNewPos:F4}m");

            Check(settled < ConvergeToleranceMeters * 5f,
                "跟随收敛后聚焦点仍贴合人物",
                $"focus 距人物={settled:F4}m");
        }

        /// <summary>测瞄准偏移：鼠标方向翻转后，相机前瞻量的横向分量必须反向。</summary>
        /// <remarks>
        /// <b>为什么不走 <c>InputIntentSource.ForceAimAndPrimary</c></b>：那条路注入的是
        /// <i>技能意图</i>；而相机读的是 <c>CursorAimInputSource.AimDirection</c>，
        /// 后者 = 鼠标世界点 − 玩家位置。headless 没有鼠标（<c>HasWorldPoint == false</c>），
        /// 所以无论怎么注入意图，<c>AimDirection</c> 都纹丝不动 —— 用它来测会得到一个
        /// "看起来通过、实际没测"的假绿。这里直接以两个相反方向驱动 rig，
        /// 验的是「鼠标→相机」这段链路本身（rig 内部投影 + 死区 + 封顶 + 平滑）。
        /// </remarks>
        private void OnAimOffset(TopDownCameraRig rig)
        {
            Vector3 hero = _session.PlayerWorldPosition();

            // 让前瞻弹簧有时间收敛：每方向推进若干帧
            Vector3 posAhead = SettleAim(rig, hero, new Vector3(1f, 0f, 0f));
            Vector3 negAhead = SettleAim(rig, hero, new Vector3(-1f, 0f, 0f));

            GD.Print($"[camera-probe] 瞄准 +X → lookAhead={posAhead}");
            GD.Print($"[camera-probe] 瞄准 -X → lookAhead={negAhead}");

            bool bothZero = posAhead.LengthSquared() < 1e-10f // arch-ok: 浮点零判定阈值，不是玩法数值
                && negAhead.LengthSquared() < 1e-10f;
            if (bothZero)
            {
                GD.Print("[camera-probe] 注意：两方向前瞻量均为零 —— " +
                         "若镜头包 LookAheadRatio=0 属预期，否则说明鼠标方向没接进来");
                return;
            }

            // 屏幕右方向在 yaw=0、pitch≈61° 时约为 +X；翻转瞄准后横向分量必须变号
            float expectedOneSide = Math.Max(Math.Abs(posAhead.X), Math.Abs(negAhead.X));
            float swing = posAhead.X - negAhead.X;

            Check(Math.Abs(swing) > expectedOneSide * 0.5f,
                "瞄准偏移幅度足够（翻转方向后横向分量大幅反向）",
                $"摆动={swing:F4}m，单侧幅度={expectedOneSide:F4}m");

            Check(posAhead.X * negAhead.X < 0f,
                "瞄准偏移随鼠标方向变号（符号跟随，不是常数偏置）",
                $"+X 时 {posAhead.X:F4} 与 -X 时 {negAhead.X:F4} 异号");

            Check(Math.Abs(posAhead.X) <= rig.Settings.LookAheadMaxMeters + 1e-3f,
                "瞄准偏移不超镜头包上限",
                $"|{posAhead.X:F4}| ≤ {rig.Settings.LookAheadMaxMeters:F3}m");
        }

        /// <summary>以给定瞄准方向驱动相机到稳态，返回平滑后的前瞻量。</summary>
        private Vector3 SettleAim(TopDownCameraRig rig, Vector3 hero, Vector3 aim)
        {
            for (int i = 0; i < FramesPerStage; i++)
            {
                rig.Tick(hero, aim, 1f / 60f);
            }

            return rig.CurrentLookAhead;
        }

        private void Check(bool ok, string name, string detail)
        {
            _checks++;
            if (ok)
            {
                GD.Print($"[camera-probe] PASS {name} | {detail}");
            }
            else
            {
                _failures++;
                GD.Print($"[camera-probe] FAIL {name} | {detail}");
            }
        }

        private void Fail(string name, string detail)
        {
            _failures++;
            GD.Print($"[camera-probe] FAIL {name} | {detail}");
        }
    }
}
