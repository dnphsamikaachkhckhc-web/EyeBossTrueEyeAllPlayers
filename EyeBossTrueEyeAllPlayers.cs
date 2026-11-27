using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace EyeBossTrueEyeAllPlayers
{
    [ApiVersion(2, 1)]
    public class EyeBossTrueEyeAllPlayers : TerrariaPlugin
    {
        public override string Name => "EyeBossTrueEyeAllPlayers";
        public override string Author => "Shokuya";
        public override string Description => "Eye of Cthulhu rework with True Eye for all players and 10% ultimate sequence.";
        public override Version Version => new Version(1, 0, 0, 0);

        private enum UltStage
        {
            None = 0,
            PullAndLock = 1,
            CloneLaser2s = 2,
            TopRightClone3s = 3,
            UfoDash10s = 4,
            RapidDash1Dmg5s = 5,
            SurrenderIdle = 6
        }

        private class BossState
        {
            public int BossNpcIndex = -1;
            public bool Phase2Triggered = false;
            public bool UltTriggered = false;
            public UltStage Stage = UltStage.None;
            public int StageTicks = 0;
            public List<int> CloneIndices = new List<int>();
            public List<int> TrueEyeIndices = new List<int>();
            public int TrueEyeSpawnCooldown = 0;
            public Vector2 SurrenderPos = Vector2.Zero;
        }

        private readonly BossState _state = new BossState();
        private readonly Dictionary<int, float> _lockedXByPlayer = new Dictionary<int, float>();
        private readonly HashSet<int> _cartDeathHandled = new HashSet<int>();

        public EyeBossTrueEyeAllPlayers(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
            ServerApi.Hooks.NpcStrike.Register(this, OnNpcStrike);
            ServerApi.Hooks.NetGetData.Register(this, OnNetGetData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                ServerApi.Hooks.NpcStrike.Deregister(this, OnNpcStrike);
                ServerApi.Hooks.NetGetData.Deregister(this, OnNetGetData);
            }
            base.Dispose(disposing);
        }

        private bool IsBossActive()
        {
            if (_state.BossNpcIndex < 0 || _state.BossNpcIndex >= Main.maxNPCs)
                return false;

            NPC boss = Main.npc[_state.BossNpcIndex];
            return boss.active && boss.type == NPCID.EyeofCthulhu;
        }

        private void FindOrSyncBoss()
        {
            if (IsBossActive())
                return;

            _state.BossNpcIndex = -1;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && npc.type == NPCID.EyeofCthulhu)
                {
                    _state.BossNpcIndex = i;
                    break;
                }
            }

            if (_state.BossNpcIndex == -1)
            {
                CleanupClonesAndEyes();
                _state.Phase2Triggered = false;
                _state.UltTriggered = false;
                _state.Stage = UltStage.None;
                _state.StageTicks = 0;
                _lockedXByPlayer.Clear();
                _cartDeathHandled.Clear();
            }
        }

        private void CleanupClonesAndEyes()
        {
            foreach (int idx in _state.CloneIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    Main.npc[idx].active = false;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                }
            }
            _state.CloneIndices.Clear();

            foreach (int idx in _state.TrueEyeIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    Main.npc[idx].active = false;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                }
            }
            _state.TrueEyeIndices.Clear();
        }

        private void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            if (args.Handled)
                return;

            FindOrSyncBoss();
            if (!IsBossActive())
                return;

            if (args.NpcId < 0 || args.NpcId >= Main.maxNPCs)
                return;

            NPC spawned = Main.npc[args.NpcId];
            if (!spawned.active)
                return;

            // ห้ามมอนสเตอร์ปกติเกิดตลอดที่บอสอยู่ ยกเว้นลูกน้องบอส (Servant of Cthulhu) และบอส/ร่างโคลน/True Eye ที่ปลั๊กอินสร้าง
            bool isAllowedNpc =
                spawned.type == NPCID.EyeofCthulhu ||
                spawned.type == NPCID.ServantofCthulhu ||
                spawned.type == NPCID.Retinazer ||
                spawned.type == NPCID.Spazmatism ||
                spawned.boss ||
                _state.CloneIndices.Contains(args.NpcId) ||
                _state.TrueEyeIndices.Contains(args.NpcId);

            if (!isAllowedNpc)
            {
                spawned.active = false;
                spawned.netSkip = -1;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", args.NpcId);
                args.Handled = true;
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (!IsBossActive())
                return;

            if (args.Npc.whoAmI == _state.BossNpcIndex && _state.Stage == UltStage.SurrenderIdle)
            {
                args.Handled = true;
            }
        }

        private void OnNetGetData(GetDataEventArgs args)
        {
            if (args.Handled || !IsBossActive())
                return;

            if (args.MsgID == PacketTypes.PlayerSpawn || args.MsgID == PacketTypes.PlayerControls)
            {
                int plrIdx = args.Msg.whoAmI;
                if (plrIdx >= 0 && plrIdx < Main.maxPlayers && _lockedXByPlayer.ContainsKey(plrIdx))
                {
                    Player p = Main.player[plrIdx];
                    if (p != null && p.active && !p.dead)
                    {
                        p.position.X = _lockedXByPlayer[plrIdx];
                        p.velocity.X = 0f;
                    }
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            FindOrSyncBoss();
            if (!IsBossActive())
                return;

            NPC boss = Main.npc[_state.BossNpcIndex];

            // กฎใช้รถรางตายทันทีเมื่อบอสอยู่
            EnforceMinecartInstantKill();

            // คุมไม่ให้มอนสเตอร์ปกติโผล่ระหว่างสู้บอส
            BlockNormalEnemiesWhileBossAlive(boss);

            // ถ้าอยู่ในสถานะยอมแพ้: นิ่งอยู่ตรงหน้าผู้เล่นและไม่ทำอะไรเลย
            if (_state.Stage == UltStage.SurrenderIdle)
            {
                HandleSurrenderIdle(boss);
                return;
            }

            // เฟส 2: ฮีลเลือด 100% ถาวรเมื่อเข้าสู่เฟส 2
            if (!_state.Phase2Triggered && (boss.ai[0] >= 1f || boss.life <= boss.lifeMax / 2))
            {
                _state.Phase2Triggered = true;
                boss.life = boss.lifeMax;
                TSPlayer.All.SendData(PacketTypes.NpcHP, "", boss.whoAmI);
                TSPlayer.All.SendInfoMessage("Eye of Cthulhu เข้าสู่เฟส 2 และฟื้นฟูพลังชีวิต 100% ถาวร!");
            }

            if (_state.Phase2Triggered)
            {
                if (boss.life < boss.lifeMax)
                {
                    boss.life = boss.lifeMax;
                    TSPlayer.All.SendData(PacketTypes.NpcHP, "", boss.whoAmI);
                }

                // True Eye ตามผู้เล่นทุกคนอย่างต่อเนื่อง
                MaintainTrueEyesForAllPlayers(boss);
            }

            // ทริกเกอร์ท่าไม้ตายเมื่อเลือดถึงหรือต่ำกว่า 10%
            if (!_state.UltTriggered && boss.life <= boss.lifeMax * 0.10f)
            {
                _state.UltTriggered = true;
                _state.Stage = UltStage.PullAndLock;
                _state.StageTicks = 0;
                _lockedXByPlayer.Clear();
                TSPlayer.All.SendWarningMessage("Eye of Cthulhu เตรียมใช้ท่าไม้ตาย! รวมตัวกันไว้!");
            }

            if (_state.UltTriggered)
            {
                RunUltimateSequence(boss);
            }
        }

        private void EnforceMinecartInstantKill()
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                TSPlayer tsPlayer = TShock.Players[i];
                if (p == null || tsPlayer == null || !p.active || tsPlayer.Dead)
                    continue;

                bool ridingMinecart = p.mount.Active &&
                    (p.mount.Type == MountID.Minecart ||
                     p.mount.Type == MountID.WoodMinecart ||
                     p.mount.Type == MountID.MinecartMech ||
                     p.mount.Type == MountID.MeowmereMinecart);

                if (ridingMinecart && !_cartDeathHandled.Contains(i))
                {
                    _cartDeathHandled.Add(i);
                    p.mount.Dismount(p);
                    tsPlayer.KillPlayer();
                    tsPlayer.SendErrorMessage("ห้ามใช้รถรางขณะสู้กับ Eye of Cthulhu! โดนลงโทษตายทันที");
                }
                else if (!ridingMinecart)
                {
                    _cartDeathHandled.Remove(i);
                }
            }
        }

        private void BlockNormalEnemiesWhileBossAlive(NPC boss)
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (i == _state.BossNpcIndex || _state.CloneIndices.Contains(i) || _state.TrueEyeIndices.Contains(i))
                    continue;

                NPC npc = Main.npc[i];
                if (!npc.active)
                    continue;

                if (npc.type == NPCID.ServantofCthulhu || npc.boss)
                    continue;

                if (!npc.friendly && npc.lifeMax > 5)
                {
                    npc.active = false;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", i);
                }
            }
        }

        private void MaintainTrueEyesForAllPlayers(NPC boss)
        {
            if (_state.TrueEyeSpawnCooldown > 0)
                _state.TrueEyeSpawnCooldown--;

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead)
                    continue;

                int existingIdx = FindTrueEyeForPlayer(i);
                if (existingIdx == -1 && _state.TrueEyeSpawnCooldown <= 0)
                {
                    int idx = NPC.NewNPC(
                        NPC.GetBossSpawnSource(i),
                        (int)(p.Center.X + 140f),
                        (int)(p.Center.Y - 120f),
                        NPCID.ServantofCthulhu,
                        0,
                        i,
                        1f,
                        0f,
                        0f
                    );

                    if (idx >= 0 && idx < Main.maxNPCs)
                    {
                        NPC eye = Main.npc[idx];
                        eye.lifeMax = 250;
                        eye.life = 250;
                        eye.damage = 28;
                        eye.knockBackResist = 0.8f;
                        eye.ai[0] = i;
                        eye.ai[1] = 1f;
                        _state.TrueEyeIndices.Add(idx);
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                        _state.TrueEyeSpawnCooldown = 30;
                    }
                }
            }

            // อัปเดตการเคลื่อนที่ของ True Eye ให้ตามผู้เล่นแต่ละคน
            List<int> aliveEyes = new List<int>();
            foreach (int idx in _state.TrueEyeIndices)
            {
                if (idx < 0 || idx >= Main.maxNPCs)
                    continue;

                NPC eye = Main.npc[idx];
                if (!eye.active)
                    continue;

                int targetPlayerIdx = (int)eye.ai[0];
                if (targetPlayerIdx >= 0 && targetPlayerIdx < Main.maxPlayers)
                {
                    Player target = Main.player[targetPlayerIdx];
                    if (target != null && target.active && !target.dead)
                    {
                        float angle = (float)(Main.GameUpdateCount * 0.05 + targetPlayerIdx * 1.3);
                        Vector2 desiredOffset = new Vector2((float)Math.Cos(angle) * 150f, (float)Math.Sin(angle) * 110f - 40f);
                        Vector2 desiredPos = target.Center + desiredOffset;
                        Vector2 toDesired = desiredPos - eye.Center;
                        eye.velocity = Vector2.Lerp(eye.velocity, toDesired * 0.08f, 0.25f);
                        if (eye.velocity.Length() > 11f)
                            eye.velocity = Vector2.Normalize(eye.velocity) * 11f;

                        eye.direction = target.Center.X >= eye.Center.X ? 1 : -1;
                        eye.spriteDirection = eye.direction;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                    }
                }
                aliveEyes.Add(idx);
            }
            _state.TrueEyeIndices = aliveEyes;
        }

        private int FindTrueEyeForPlayer(int playerIndex)
        {
            foreach (int idx in _state.TrueEyeIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs)
                {
                    NPC eye = Main.npc[idx];
                    if (eye.active && (int)eye.ai[0] == playerIndex)
                        return idx;
                }
            }
            return -1;
        }

        private Vector2 GetAlivePlayersCenter()
        {
            Vector2 sum = Vector2.Zero;
            int count = 0;
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p != null && p.active && !p.dead)
                {
                    sum += p.Center;
                    count++;
                }
            }
            return count > 0 ? (sum / count) : Main.npc[_state.BossNpcIndex].Center;
        }

        private void LockHorizontalMovementForPlayers(bool enable)
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead)
                    continue;

                if (enable)
                {
                    if (!_lockedXByPlayer.ContainsKey(i))
                        _lockedXByPlayer[i] = p.position.X;

                    p.position.X = _lockedXByPlayer[i];
                    p.velocity.X = 0f;
                    // คงความสามารถกระโดด (velocity.Y ไม่ถูกบล็อกแนวตั้ง)
                    TSPlayer.All.SendData(PacketTypes.PlayerControls, "", i);
                }
                else
                {
                    _lockedXByPlayer.Remove(i);
                }
            }
        }

        private void RunUltimateSequence(NPC boss)
        {
            _state.StageTicks++;
            Vector2 groupCenter = GetAlivePlayersCenter();

            switch (_state.Stage)
            {
                case UltStage.PullAndLock:
                {
                    // ดึงผู้เล่นรวมจุดกึ่งกลางและล็อกการเดินซ้ายขวา (ยังกระโดดได้)
                    for (int i = 0; i < Main.maxPlayers; i++)
                    {
                        Player p = Main.player[i];
                        if (p != null && p.active && !p.dead)
                        {
                            Vector2 pullDir = groupCenter - p.Center;
                            if (pullDir.Length() > 24f)
                            {
                                p.velocity.X = MathHelper.Clamp(pullDir.X * 0.18f, -12f, 12f);
                            }
                            else
                            {
                                p.velocity.X = 0f;
                            }
                        }
                    }

                    // ซ่อนบอสจริง
                    boss.dontTakeDamage = true;
                    boss.alpha = 255;
                    boss.velocity = Vector2.Zero;
                    boss.Center = groupCenter + new Vector2(0f, -450f);
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                    if (_state.StageTicks >= 45)
                    {
                        for (int i = 0; i < Main.maxPlayers; i++)
                        {
                            Player p = Main.player[i];
                            if (p != null && p.active && !p.dead)
                                _lockedXByPlayer[i] = p.position.X;
                        }

                        SpawnClonesAroundPlayers(groupCenter, 4);
                        _state.Stage = UltStage.CloneLaser2s;
                        _state.StageTicks = 0;
                    }
                    break;
                }

                case UltStage.CloneLaser2s:
                {
                    // ห้ามเดินซ้ายขวาแต่กระโดดได้ + บอสจริงถูกซ่อน
                    LockHorizontalMovementForPlayers(true);
                    boss.dontTakeDamage = true;
                    boss.alpha = 255;
                    boss.velocity = Vector2.Zero;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                    // ร่างโคลนยิงเลเซอร์ตรงตามทิศทางที่มอง นาน 2 วินาที (120 ticks)
                    FireCloneLasersAlongFacing(groupCenter);

                    if (_state.StageTicks >= 120)
                    {
                        KeepOnlyTopRightClone(groupCenter);
                        _state.Stage = UltStage.TopRightClone3s;
                        _state.StageTicks = 0;
                    }
                    break;
                }

                case UltStage.TopRightClone3s:
                {
                    // เหลือโคลนตัวเดียวขวาบน ยิงจากด้านในจับเวลา 3 วินาที (180 ticks)
                    LockHorizontalMovementForPlayers(true);
                    boss.dontTakeDamage = true;
                    boss.alpha = 255;
                    boss.velocity = Vector2.Zero;

                    if (_state.CloneIndices.Count > 0)
                    {
                        int cloneIdx = _state.CloneIndices[0];
                        if (cloneIdx >= 0 && cloneIdx < Main.maxNPCs && Main.npc[cloneIdx].active)
                        {
                            NPC clone = Main.npc[cloneIdx];
                            Vector2 topRightInside = groupCenter + new Vector2(220f, -180f);
                            clone.Center = Vector2.Lerp(clone.Center, topRightInside, 0.25f);
                            clone.velocity = Vector2.Zero;
                            clone.direction = -1;
                            clone.spriteDirection = -1;
                            TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", cloneIdx);

                            if (_state.StageTicks % 20 == 0)
                            {
                                FireSingleLaserFromClone(clone, new Vector2(-0.75f, 0.66f));
                            }
                        }
                    }

                    if (_state.StageTicks >= 180)
                    {
                        CleanupClonesAndEyes();
                        LockHorizontalMovementForPlayers(false);

                        // เผยตัวบอสจริงและพุ่งไล่ผู้เล่นแบบ UFO ความเร็วสูงระดับรองเท้าวิ่งเร็วระดับสูง 10 วินาที (600 ticks) โดยไม่ยิงก
