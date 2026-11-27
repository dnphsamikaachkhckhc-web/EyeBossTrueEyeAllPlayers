using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
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
        public override string Description => "Eye of Cthulhu rework with True Eye above players, 10s Phantasmal Sphere with 5s warning, and 2-part ultimate sequence.";
        public override Version Version => new Version(1, 0, 0, 0);

        private enum UltStage
        {
            None = 0,
            Ult1_TeleportAndWait5s = 1,
            Ult1_ClonesCharge3s = 2,
            Ult1_ClonesLaser3s = 3,
            Ult2_TopRightHoverLaser3s = 4,
            Ult2_UfoSweepLaser10s = 5,
            RapidDash1Dmg5s = 6,
            SurrenderIdle = 7
        }

        private class SphereAttackState
        {
            public int ProjectileIndex = -1;
            public int TargetPlayerIndex = -1;
            public int TelegraphTicks = 0;
            public bool HasFired = false;
            public Vector2 AimDirection = Vector2.Zero;
        }

        private class BossState
        {
            public int BossNpcIndex = -1;
            public bool Phase2Triggered = false;
            public bool UltTriggered = false;
            public UltStage Stage = UltStage.None;
            public int StageTicks = 0;
            public int LeftBottomCloneIdx = -1;
            public int TopRightCloneIdx = -1;
            public Dictionary<int, int> TrueEyeByPlayer = new Dictionary<int, int>();
            public Dictionary<int, int> SphereCycleTicksByPlayer = new Dictionary<int, int>();
            public List<SphereAttackState> ActiveSpheres = new List<SphereAttackState>();
            public Vector2 GatherPoint = Vector2.Zero;
            public Vector2 UfoSweepPos = Vector2.Zero;
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
            return boss != null && boss.active && boss.type == NPCID.EyeofCthulhu;
        }

        private void FindOrSyncBoss()
        {
            if (IsBossActive())
                return;

            _state.BossNpcIndex = -1;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc != null && npc.active && npc.type == NPCID.EyeofCthulhu)
                {
                    _state.BossNpcIndex = i;
                    break;
                }
            }

            if (_state.BossNpcIndex == -1)
            {
                CleanupClonesAndEyes();
                CleanupPhantasmalSpheres();
                _state.Phase2Triggered = false;
                _state.UltTriggered = false;
                _state.Stage = UltStage.None;
                _state.StageTicks = 0;
                _state.GatherPoint = Vector2.Zero;
                _state.UfoSweepPos = Vector2.Zero;
                _state.SphereCycleTicksByPlayer.Clear();
                _lockedXByPlayer.Clear();
                _cartDeathHandled.Clear();
            }
        }

        private void DespawnNpcIfActive(ref int npcIdx)
        {
            if (npcIdx >= 0 && npcIdx < Main.maxNPCs && Main.npc[npcIdx] != null && Main.npc[npcIdx].active)
            {
                Main.npc[npcIdx].active = false;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", npcIdx);
            }
            npcIdx = -1;
        }

        private void CleanupTrueEyesOnly()
        {
            foreach (var kv in _state.TrueEyeByPlayer)
            {
                int idx = kv.Value;
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx] != null && Main.npc[idx].active)
                {
                    Main.npc[idx].active = false;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                }
            }
            _state.TrueEyeByPlayer.Clear();
            _state.SphereCycleTicksByPlayer.Clear();
        }

        private void CleanupClonesAndEyes()
        {
            DespawnNpcIfActive(ref _state.LeftBottomCloneIdx);
            DespawnNpcIfActive(ref _state.TopRightCloneIdx);
            CleanupTrueEyesOnly();
        }

        private void CleanupPhantasmalSpheres()
        {
            foreach (var s in _state.ActiveSpheres)
            {
                if (s.ProjectileIndex >= 0 && s.ProjectileIndex < Main.maxProjectiles)
                {
                    Projectile proj = Main.projectile[s.ProjectileIndex];
                    if (proj != null && proj.active)
                    {
                        proj.Kill();
                        NetMessage.SendData(29, -1, -1, null, s.ProjectileIndex, proj.owner);
                    }
                }
            }
            _state.ActiveSpheres.Clear();
        }

        private bool IsManagedNpc(int npcId)
        {
            return npcId == _state.LeftBottomCloneIdx ||
                   npcId == _state.TopRightCloneIdx ||
                   _state.TrueEyeByPlayer.ContainsValue(npcId);
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
            if (spawned == null || !spawned.active)
                return;

            bool isAllowedNpc =
                spawned.type == NPCID.EyeofCthulhu ||
                spawned.type == NPCID.ServantofCthulhu ||
                spawned.type == NPCID.Retinazer ||
                spawned.type == NPCID.Spazmatism ||
                spawned.boss ||
                IsManagedNpc(args.NpcId);

            if (!isAllowedNpc)
            {
                spawned.active = false;
                NetMessage.SendData(23, -1, -1, null, args.NpcId);
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", args.NpcId);
                args.Handled = true;
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (!IsBossActive() || args.Npc == null)
                return;

            if (args.Npc.whoAmI == _state.BossNpcIndex)
            {
                if (_state.UltTriggered || _state.Stage == UltStage.SurrenderIdle)
                {
                    args.Handled = true;
                }
                return;
            }

            if (args.Npc.whoAmI == _state.LeftBottomCloneIdx || args.Npc.whoAmI == _state.TopRightCloneIdx)
            {
                args.Handled = true;
            }
        }

        private void OnNetGetData(GetDataEventArgs args)
        {
            if (args.Handled || !IsBossActive())
                return;

            if (args.MsgID == PacketTypes.PlayerSpawn || args.MsgID == PacketTypes.PlayerUpdate)
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

            EnforceMinecartInstantKill();
            BlockNormalEnemiesWhileBossAlive(boss);

            if (_state.Stage == UltStage.SurrenderIdle)
            {
                HandleSurrenderIdle(boss);
                return;
            }

            if (!_state.Phase2Triggered && (boss.ai[0] >= 1f || boss.life <= boss.lifeMax / 2))
            {
                _state.Phase2Triggered = true;
                boss.life = boss.lifeMax;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);
                TSPlayer.All.SendInfoMessage("Eye of Cthulhu เข้าสู่เฟส 2 และฟื้นฟูพลังชีวิต 1 ครั้ง!");
            }

            if (!_state.UltTriggered && _state.Phase2Triggered)
            {
                MaintainTrueEyesAbovePlayers();
                UpdatePhantasmalSphereAttacks();
            }

            if (!_state.UltTriggered && boss.life <= boss.lifeMax * 0.10f)
            {
                CleanupTrueEyesOnly();
                CleanupPhantasmalSpheres();

                _state.UltTriggered = true;
                _state.Stage = UltStage.Ult1_TeleportAndWait5s;
                _state.StageTicks = 0;
                _state.GatherPoint = GetAlivePlayersCenter();
                _lockedXByPlayer.Clear();
                TeleportPlayersToGatherPoint(_state.GatherPoint);
                TSPlayer.All.SendWarningMessage("Eye of Cthulhu เข้าสู่ท่าไม้ตายที่ 1! ห้ามเดินหรือวิ่ง แต่กระโดดและยิงได้!");
            }

            if (_state.UltTriggered)
            {
                RunUltimateSequence(boss);
            }
        }

        private void TeleportPlayersToGatherPoint(Vector2 gatherCenter)
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                TSPlayer tsPlayer = TShock.Players[i];
                if (p == null || tsPlayer == null || !p.active || tsPlayer.Dead)
                    continue;

                float footX = gatherCenter.X - p.width * 0.5f;
                float footY = gatherCenter.Y - p.height;
                p.Teleport(new Vector2(footX, footY), 1);
                p.velocity = Vector2.Zero;
                _lockedXByPlayer[i] = footX;
                TSPlayer.All.SendData(PacketTypes.PlayerUpdate, "", i);
            }
        }

        private void LockHorizontalOnlyForPlayers()
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead)
                    continue;

                if (!_lockedXByPlayer.ContainsKey(i))
                    _lockedXByPlayer[i] = p.position.X;

                p.position.X = _lockedXByPlayer[i];
                p.velocity.X = 0f;
                TSPlayer.All.SendData(PacketTypes.PlayerUpdate, "", i);
            }
        }

        private void UnlockAllPlayersMovement()
        {
            if (_lockedXByPlayer.Count > 0)
            {
                _lockedXByPlayer.Clear();
                for (int i = 0; i < Main.maxPlayers; i++)
                {
                    Player p = Main.player[i];
                    if (p != null && p.active && !p.dead)
                    {
                        TSPlayer.All.SendData(PacketTypes.PlayerUpdate, "", i);
                    }
                }
            }
        }

        private void HideBossInvulnerable(NPC boss, Vector2 center)
        {
            boss.dontTakeDamage = true;
            boss.alpha = 255;
            boss.velocity = Vector2.Zero;
            boss.Center = center + new Vector2(0f, -540f);
            TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);
        }

        private NPC CreateCloneNpc(Vector2 pos, int dir, float rotation)
        {
            int idx = NPC.NewNPC(
                new EntitySource_SpawnNPC(),
                (int)pos.X,
                (int)pos.Y,
                NPCID.EyeofCthulhu,
                0
            );

            if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx] != null)
            {
                NPC clone = Main.npc[idx];
                clone.lifeMax = 99999;
                clone.life = 99999;
                clone.damage = 35;
                clone.dontTakeDamage = true;
                clone.noGravity = true;
                clone.noTileCollide = true;
                clone.alpha = 0;
                clone.direction = dir;
                clone.spriteDirection = dir;
                clone.rotation = rotation;
                clone.ai[0] = 3f;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                return clone;
            }
            return null;
        }

        private void SpawnUlt1Clones(Vector2 center)
        {
            DespawnNpcIfActive(ref _state.LeftBottomCloneIdx);
            DespawnNpcIfActive(ref _state.TopRightCloneIdx);

            Vector2 leftBottomPos = center + new Vector2(-380f, 50f);
            Vector2 topRightPos = center + new Vector2(380f, -240f);

            NPC leftBottom = CreateCloneNpc(leftBottomPos, 1, 0f);
            if (leftBottom != null)
                _state.LeftBottomCloneIdx = leftBottom.whoAmI;

            NPC topRight = CreateCloneNpc(topRightPos, -1, 0f);
            if (topRight != null)
                _state.TopRightCloneIdx = topRight.whoAmI;
        }

        private void UpdateUlt1ClonesPositionAndAim(Vector2 center, bool fireLasers)
        {
            Player samplePlayer = FindAnyAlivePlayer();
            float groundY = samplePlayer != null ? samplePlayer.position.Y + samplePlayer.height * 0.5f : center.Y;

            if (_state.LeftBottomCloneIdx >= 0 && _state.LeftBottomCloneIdx < Main.maxNPCs && Main.npc[_state.LeftBottomCloneIdx] != null && Main.npc[_state.LeftBottomCloneIdx].active)
            {
                NPC lb = Main.npc[_state.LeftBottomCloneIdx];
                lb.Center = center + new Vector2(-380f, 50f);
                lb.velocity = Vector2.Zero;
                lb.direction = 1;
                lb.spriteDirection = 1;
                lb.rotation = 0f;
                lb.dontTakeDamage = true;
                lb.noGravity = true;
                lb.noTileCollide = true;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", lb.whoAmI);

                if (fireLasers && _state.StageTicks % 12 == 0)
                {
                    float targetX = samplePlayer != null ? samplePlayer.Center.X : center.X;
                    Vector2 floorTarget = new Vector2(targetX, groundY);
                    Vector2 dir = floorTarget - lb.Center;
                    if (dir.Length() > 0.01f)
                    {
                        dir.Normalize();
                        FireLaserProjectile(lb.Center, dir * 16f, 28);
                    }
                }
            }

            if (_state.TopRightCloneIdx >= 0 && _state.TopRightCloneIdx < Main.maxNPCs && Main.npc[_state.TopRightCloneIdx] != null && Main.npc[_state.TopRightCloneIdx].active)
            {
                NPC tr = Main.npc[_state.TopRightCloneIdx];
                tr.Center = center + new Vector2(380f, -260f);
                tr.velocity = Vector2.Zero;
                tr.direction = -1;
                tr.spriteDirection = -1;
                tr.rotation = 0f;
                tr.dontTakeDamage = true;
                tr.noGravity = true;
                tr.noTileCollide = true;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", tr.whoAmI);

                if (fireLasers && _state.StageTicks % 12 == 0)
                {
                    Vector2 playerPos = samplePlayer != null ? samplePlayer.Center : center;
                    Vector2 topScreenOrigin = new Vector2(tr.Center.X, playerPos.Y - 420f);
                    Vector2 dir = playerPos - topScreenOrigin;
                    if (dir.Length() > 0.01f)
                    {
                        dir.Normalize();
                        FireLaserProjectile(topScreenOrigin, dir * 16f, 28);
                    }
                }
            }
        }

        private void FireLaserProjectile(Vector2 origin, Vector2 velocity, int damage)
        {
            int projIdx = Projectile.NewProjectile(
                new EntitySource_SpawnNPC(),
                origin,
                velocity,
                ProjectileID.DeathLaser,
                damage,
                0f,
                Main.myPlayer
            );

            if (projIdx >= 0 && projIdx < Main.maxProjectiles && Main.projectile[projIdx] != null)
            {
                Projectile proj = Main.projectile[projIdx];
                proj.hostile = true;
                proj.friendly = false;
                proj.tileCollide = false;
                TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", projIdx);
            }
        }

        private Player FindAnyAlivePlayer()
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p != null && p.active && !p.dead)
                    return p;
            }
            return null;
        }

        private void RunUltimateSequence(NPC boss)
        {
            _state.StageTicks++;
            Vector2 groupCenter = GetAlivePlayersCenter();

            switch (_state.Stage)
            {
                case UltStage.Ult1_TeleportAndWait5s:
                {
                    if (_state.GatherPoint == Vector2.Zero)
                        _state.GatherPoint = groupCenter;

                    HideBossInvulnerable(boss, _state.GatherPoint);
                    LockHorizontalOnlyForPlayers();

                    if (_state.StageTicks >= 300)
                    {
                        SpawnUlt1Clones(_state.GatherPoint);
                        _state.Stage = UltStage.Ult1_ClonesCharge3s;
                        _state.StageTicks = 0;
                        TSPlayer.All.SendWarningMessage("ร่างโคลนโผล่มาชาร์จเลเซอร์ 3 วินาที!");
                    }
                    break;
                }

                case UltStage.Ult1_ClonesCharge3s:
                {
                    HideBossInvulnerable(boss, _state.GatherPoint);
                    LockHorizontalOnlyForPlayers();
                    UpdateUlt1ClonesPositionAndAim(_state.GatherPoint, fireLasers: false);

                    if (_state.StageTicks >= 180)
                    {
                        _state.Stage = UltStage.Ult1_ClonesLaser3s;
                        _state.StageTicks = 0;
                        TSPlayer.All.SendWarningMessage("ร่างโคลนยิงเลเซอร์แล้ว! กระโดดหลบให้ดี!");
                    }
                    break;
                }

                case UltStage.Ult1_ClonesLaser3s:
                {
                    HideBossInvulnerable(boss, _state.GatherPoint);
                    LockHorizontalOnlyForPlayers();
                    UpdateUlt1ClonesPositionAndAim(_state.GatherPoint, fireLasers: true);

                    if (_state.StageTicks >= 180)
                    {
                        DespawnNpcIfActive(ref _state.LeftBottomCloneIdx);
                        UnlockAllPlayersMovement();

                        if (_state.TopRightCloneIdx < 0 || _state.TopRightCloneIdx >= Main.maxNPCs || Main.npc[_state.TopRightCloneIdx] == null || !Main.npc[_state.TopRightCloneIdx].active)
                        {
                            NPC tr = CreateCloneNpc(_state.GatherPoint + new Vector2(360f, -260f), -1, MathHelper.PiOver2);
                            if (tr != null)
                                _state.TopRightCloneIdx = tr.whoAmI;
                        }
                        else
                        {
                            NPC tr = Main.npc[_state.TopRightCloneIdx];
                            tr.rotation = MathHelper.PiOver2;
                            TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", tr.whoAmI);
                        }

                        _state.Stage = UltStage.Ult2_TopRightHoverLaser3s;
                        _state.StageTicks = 0;
                        TSPlayer.All.SendWarningMessage("เดินและวิ่งได้แล้ว! เตรียมรับเลเซอร์ลงพื้น 3 วินาทีก่อนออกตัว!");
                    }
                    break;
                }

                case UltStage.Ult2_TopRightHoverLaser3s:
                {
                    HideBossInvulnerable(boss, groupCenter);
                    UnlockAllPlayersMovement();

                    if (_state.TopRightCloneIdx >= 0 && _state.TopRightCloneIdx < Main.maxNPCs && Main.npc[_state.TopRightCloneIdx] != null && Main.npc[_state.TopRightCloneIdx].active)
                    {
                        NPC tr = Main.npc[_state.TopRightCloneIdx];
                        Vector2 hoverPos = groupCenter + new Vector2(360f, -260f);
                        tr.Center = Vector2.Lerp(tr.Center, hoverPos, 0.25f);
                        tr.velocity = Vector2.Zero;
                        tr.rotation = MathHelper.PiOver2;
                        tr.dontTakeDamage = true;
                        tr.noGravity = true;
                        tr.noTileCollide = true;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", tr.whoAmI);

                        if (_state.StageTicks % 10 == 0)
                        {
                            FireLaserProjectile(tr.Center + new Vector2(0f, 24f), new Vector2(0f, 18f), 30);
                        }
                    }

                    if (_state.StageTicks >= 180)
                    {
                        if (_state.TopRightCloneIdx >= 0 && _state.TopRightCloneIdx < Main.maxNPCs && Main.npc[_state.TopRightCloneIdx] != null && Main.npc[_state.TopRightCloneIdx].active)
                            _state.UfoSweepPos = Main.npc[_state.TopRightCloneIdx].Center;
                        else
                            _state.UfoSweepPos = groupCenter + new Vector2(260f, -260f);

                        _state.Stage = UltStage.Ult2_UfoSweepLaser10s;
                        _state.StageTicks = 0;
                        TSPlayer.All.SendWarningMessage("บอสออกตัวยิงเลเซอร์ไล่แบบ UFO 10 วินาที!");
                    }
                    break;
                }

                case UltStage.Ult2_UfoSweepLaser10s:
                {
                    HideBossInvulnerable(boss, groupCenter);
                    UnlockAllPlayersMovement();

                    if (_state.TopRightCloneIdx >= 0 && _state.TopRightCloneIdx < Main.maxNPCs && Main.npc[_state.TopRightCloneIdx] != null && Main.npc[_state.TopRightCloneIdx].active)
                    {
                        NPC tr = Main.npc[_state.TopRightCloneIdx];
                        Vector2 desiredPos = new Vector2(groupCenter.X + (float)Math.Sin(_state.StageTicks * 0.045) * 280f, groupCenter.Y - 260f);
                        Vector2 toDesired = desiredPos - tr.Center;
                        tr.velocity = Vector2.Lerp(tr.velocity, toDesired * 0.12f, 0.25f);
                        tr.rotation = MathHelper.PiOver2;
                        tr.dontTakeDamage = true;
                        tr.noGravity = true;
                        tr.noTileCollide = true;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", tr.whoAmI);

                        if (_state.StageTicks % 10 == 0)
                        {
                            FireLaserProjectile(tr.Center + new Vector2(0f, 24f), new Vector2(0f, 18f), 30);
                        }
                    }

                    if (_state.StageTicks >= 600)
                    {
                        DespawnNpcIfActive(ref _state.TopRightCloneIdx);
                        boss.alpha = 0;
                        boss.dontTakeDamage = false;
                        boss.noTileCollide = true;
                        boss.damage = 1;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                        _state.Stage = UltStage.RapidDash1Dmg5s;
                        _state.StageTicks = 0;
                    }
                    break;
                }

                case UltStage.RapidDash1Dmg5s:
                {
                    UnlockAllPlayersMovement();
                    boss.dontTakeDamage = false;
                    boss.alpha = 0;
                    boss.noTileCollide = true;
                    boss.damage = 1;

                    Vector2 toTarget = groupCenter - boss.Center;
                    if (toTarget.Length() > 1f)
                    {
                        Vector2 dir = Vector2.Normalize(toTarget);
                        boss.velocity = Vector2.Lerp(boss.velocity, dir * 26f, 0.22f);
                        boss.direction = dir.X >= 0f ? 1 : -1;
                        boss.spriteDirection = boss.direction;
                    }

                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                    if (_state.StageTicks >= 300)
                    {
                        _state.Stage = UltStage.SurrenderIdle;
                        _state.StageTicks = 0;
                        _state.SurrenderPos = boss.Center;
                        CleanupClonesAndEyes();
                        CleanupPhantasmalSpheres();
                        UnlockAllPlayersMovement();
                        TSPlayer.All.SendSuccessMessage("Eye of Cthulhu ยอมแพ้แล้ว!");
                    }
                    break;
                }

                case UltStage.SurrenderIdle:
                {
                    HandleSurrenderIdle(boss);
                    break;
                }
            }
        }

        private void HandleSurrenderIdle(NPC boss)
        {
            boss.dontTakeDamage = true;
            boss.velocity = Vector2.Zero;
            boss.Center = _state.SurrenderPos;
            TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);
        }

        private void MaintainTrueEyesAbovePlayers()
        {
            const int tenSecondsTicks = 600;

            List<int> toRemovePlayers = new List<int>();
            foreach (var kv in _state.TrueEyeByPlayer)
            {
                int plrIdx = kv.Key;
                int eyeIdx = kv.Value;
                Player p = (plrIdx >= 0 && plrIdx < Main.maxPlayers) ? Main.player[plrIdx] : null;
                bool playerAlive = p != null && p.active && !p.dead;

                if (!playerAlive)
                {
                    if (eyeIdx >= 0 && eyeIdx < Main.maxNPCs && Main.npc[eyeIdx] != null && Main.npc[eyeIdx].active)
                    {
                        Main.npc[eyeIdx].active = false;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", eyeIdx);
                    }
                    toRemovePlayers.Add(plrIdx);
                }
            }
            foreach (int plrIdx in toRemovePlayers)
            {
                _state.TrueEyeByPlayer.Remove(plrIdx);
                _state.SphereCycleTicksByPlayer.Remove(plrIdx);
            }

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead)
                    continue;

                int eyeIdx = -1;
                if (!_state.TrueEyeByPlayer.TryGetValue(i, out eyeIdx) ||
                    eyeIdx < 0 || eyeIdx >= Main.maxNPCs || Main.npc[eyeIdx] == null || !Main.npc[eyeIdx].active)
                {
                    int createdIdx = NPC.NewNPC(
                        new EntitySource_SpawnNPC(),
                        (int)p.Center.X,
                        (int)(p.Center.Y - 120f),
                        NPCID.ServantofCthulhu,
                        0,
                        i,
                        1f,
                        0f,
                        0f
                    );

                    if (createdIdx >= 0 && createdIdx < Main.maxNPCs && Main.npc[createdIdx] != null)
                    {
                        NPC eye = Main.npc[createdIdx];
                        eye.lifeMax = 350;
                        eye.life = 350;
                        eye.damage = 30;
                        eye.scale = 1.45f;
                        eye.noGravity = true;
                        eye.noTileCollide = true;
                        eye.knockBackResist = 0.85f;
                        eye.ai[0] = i;
                        _state.TrueEyeByPlayer[i] = createdIdx;
                        if (!_state.SphereCycleTicksByPlayer.ContainsKey(i))
                            _state.SphereCycleTicksByPlayer[i] = 0;

                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", createdIdx);
                        eyeIdx = createdIdx;
                    }
                }

                if (eyeIdx >= 0 && eyeIdx < Main.maxNPCs && Main.npc[eyeIdx] != null && Main.npc[eyeIdx].active)
                {
                    NPC eye = Main.npc[eyeIdx];
                    float bob = (float)Math.Sin(Main.GameUpdateCount * 0.08 + i) * 10f;
                    Vector2 aboveHeadPos = p.Center + new Vector2(0f, -115f + bob);
                    Vector2 toAbove = aboveHeadPos - eye.Center;

                    eye.velocity = toAbove * 0.25f;
                    eye.Center += eye.velocity;
                    eye.velocity = Vector2.Zero;
                    eye.noGravity = true;
                    eye.noTileCollide = true;
                    eye.direction = p.Center.X >= eye.Center.X ? 1 : -1;
                    eye.spriteDirection = eye.direction;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", eyeIdx);

                    int cycle = _state.SphereCycleTicksByPlayer.TryGetValue(i, out int curTick) ? curTick + 1 : 1;
                    if (cycle >= tenSecondsTicks)
                    {
                        cycle = 0;
                        SpawnPhantasmalSphereFromEye(eye, p, i);
                    }
                    _state.SphereCycleTicksByPlayer[i] = cycle;
                }
            }
        }

        private void SpawnPhantasmalSphereFromEye(NPC eye, Player target, int targetPlayerIndex)
        {
            Vector2 spawnPos = eye.Center + new Vector2(0f, -24f);
            Vector2 dirToPlayer = target.Center - spawnPos;
            if (dirToPlayer.Length() < 0.01f)
                dirToPlayer = new Vector2(0f, 1f);
            else
                dirToPlayer.Normalize();

            int projIdx = Projectile.NewProjectile(
                new EntitySource_SpawnNPC(),
                spawnPos,
                Vector2.Zero,
                ProjectileID.PhantasmalSphere,
                32,
                0f,
                Main.myPlayer
            );

            if (projIdx >= 0 && projIdx < Main.maxProjectiles && Main.projectile[projIdx] != null)
            {
                Projectile proj = Main.projectile[projIdx];
                proj.hostile = true;
                proj.friendly = false;
                proj.tileCollide = false;
                proj.timeLeft = 900;
                TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", projIdx);

                _state.ActiveSpheres.Add(new SphereAttackState
                {
                    ProjectileIndex = projIdx,
                    TargetPlayerIndex = targetPlayerIndex,
                    TelegraphTicks = 0,
                    HasFired = false,
                    AimDirection = dirToPlayer
                });

                if (targetPlayerIndex >= 0 && targetPlayerIndex < Main.maxPlayers && TShock.Players[targetPlayerIndex] != null)
                    TShock.Players[targetPlayerIndex].SendWarningMessage("True Eye เตรียมยิง Phantasmal Sphere ใน 5 วินาที!");
            }
        }

        private void UpdatePhantasmalSphereAttacks()
        {
            const int fiveSecondsWarningTicks = 300;
            const float shootSpeed = 16.5f;

            List<SphereAttackState> remaining = new List<SphereAttackState>();

            foreach (var s in _state.ActiveSpheres)
            {
                if (s.ProjectileIndex < 0 || s.ProjectileIndex >= Main.maxProjectiles)
                    continue;

                Projectile proj = Main.projectile[s.ProjectileIndex];
                if (proj == null || !proj.active || proj.type != ProjectileID.PhantasmalSphere)
                    continue;

                Player target = (s.TargetPlayerIndex >= 0 && s.TargetPlayerIndex < Main.maxPlayers)
                    ? Main.player[s.TargetPlayerIndex]
                    : null;

                if (!s.HasFired)
                {
                    s.TelegraphTicks++;

                    if (target != null && target.active && !target.dead)
                    {
                        Vector2 hoverAnchor = target.Center + new Vector2(0f, -155f);
                        Vector2 toAnchor = hoverAnchor - proj.Center;
                        proj.velocity = toAnchor * 0.2f;
                        proj.Center += proj.velocity;
                        proj.velocity = Vector2.Zero;

                        Vector2 aim = target.Center - proj.Center;
                        if (aim.Length() > 0.01f)
                        {
                            aim.Normalize();
                            s.AimDirection = aim;
                        }
                    }

                    proj.timeLeft = 600;
                    TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", s.ProjectileIndex);

                    if (s.TelegraphTicks >= fiveSecondsWarningTicks)
                    {
                        s.HasFired = true;
                        Vector2 launchDir = s.AimDirection.LengthSquared() > 0.001f
                            ? s.AimDirection
                            : new Vector2(0f, 1f);

                        proj.velocity = launchDir * shootSpeed;
                        proj.timeLeft = 240;
                        TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", s.ProjectileIndex);
                    }
                }
                else
                {
                    Vector2 launchDir = s.AimDirection.LengthSquared() > 0.001f
                        ? s.AimDirection
                        : new Vector2(0f, 1f);
                    proj.velocity = launchDir * shootSpeed;
                    TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", s.ProjectileIndex);
                }

                remaining.Add(s);
            }

            _state.ActiveSpheres = remaining;
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
                if (i == _state.BossNpcIndex || IsManagedNpc(i))
                    continue;

                NPC npc = Main.npc[i];
                if (npc == null || !npc.active)
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

        private void HandleSurrenderIdle(NPC boss)
        {
            boss.dontTakeDamage = true;
            boss.velocity = Vector2.Zero;
            boss.Center = _state.SurrenderPos;
            TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);
        }
    }
}
