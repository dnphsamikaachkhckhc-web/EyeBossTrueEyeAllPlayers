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
        public override string Description => "Eye of Cthulhu rework with True Eye above players, 10s Phantasmal Sphere with 5s warning, and 10% ultimate sequence.";
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

        private class SphereAttackState
        {
            public int ProjectileIndex = -1;
            public int TargetPlayerIndex = -1;
            public int TelegraphTicks = 0; // 300 ticks = 5 วินาทีเตือน
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
            public List<int> CloneIndices = new List<int>();
            public Dictionary<int, int> TrueEyeByPlayer = new Dictionary<int, int>();
            public Dictionary<int, int> SphereCycleTicksByPlayer = new Dictionary<int, int>(); // 600 ticks = 10 วินาที
            public List<SphereAttackState> ActiveSpheres = new List<SphereAttackState>();
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
                CleanupPhantasmalSpheres();
                _state.Phase2Triggered = false;
                _state.UltTriggered = false;
                _state.Stage = UltStage.None;
                _state.StageTicks = 0;
                _state.SphereCycleTicksByPlayer.Clear();
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

            foreach (var kv in _state.TrueEyeByPlayer)
            {
                int idx = kv.Value;
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    Main.npc[idx].active = false;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                }
            }
            _state.TrueEyeByPlayer.Clear();
        }

        private void CleanupPhantasmalSpheres()
        {
            foreach (var s in _state.ActiveSpheres)
            {
                if (s.ProjectileIndex >= 0 && s.ProjectileIndex < Main.maxProjectiles)
                {
                    Projectile proj = Main.projectile[s.ProjectileIndex];
                    if (proj.active)
                    {
                        proj.Kill();
                        NetMessage.SendData(29, -1, -1, null, s.ProjectileIndex, proj.owner);
                    }
                }
            }
            _state.ActiveSpheres.Clear();
        }

        private bool IsManagedEye(int npcId)
        {
            return _state.CloneIndices.Contains(npcId) || _state.TrueEyeByPlayer.ContainsValue(npcId);
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

            bool isAllowedNpc =
                spawned.type == NPCID.EyeofCthulhu ||
                spawned.type == NPCID.ServantofCthulhu ||
                spawned.type == NPCID.Retinazer ||
                spawned.type == NPCID.Spazmatism ||
                spawned.boss ||
                IsManagedEye(args.NpcId);

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

            // ฟื้นเลือดเต็มแค่รอบเดียวตอนเข้าเฟส 2
            if (!_state.Phase2Triggered && (boss.ai[0] >= 1f || boss.life <= boss.lifeMax / 2))
            {
                _state.Phase2Triggered = true;
                boss.life = boss.lifeMax;
                TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);
                TSPlayer.All.SendInfoMessage("Eye of Cthulhu เข้าสู่เฟส 2 และฟื้นฟูพลังชีวิต 1 ครั้ง!");
            }

            if (_state.Phase2Triggered)
            {
                MaintainTrueEyesAbovePlayers();
                UpdatePhantasmalSphereAttacks();
            }

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

        private void MaintainTrueEyesAbovePlayers()
        {
            const int tenSecondsTicks = 600; // 60 ticks/sec * 10 sec

            // ลบ True Eye ของผู้เล่นที่ตายหรือออกจากเกม
            List<int> toRemovePlayers = new List<int>();
            foreach (var kv in _state.TrueEyeByPlayer)
            {
                int plrIdx = kv.Key;
                int eyeIdx = kv.Value;
                Player p = (plrIdx >= 0 && plrIdx < Main.maxPlayers) ? Main.player[plrIdx] : null;
                bool playerAlive = p != null && p.active && !p.dead;

                if (!playerAlive)
                {
                    if (eyeIdx >= 0 && eyeIdx < Main.maxNPCs && Main.npc[eyeIdx].active)
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
                    eyeIdx < 0 || eyeIdx >= Main.maxNPCs || !Main.npc[eyeIdx].active)
                {
                    // เสก True Eye (ใช้ ServantofCthulhu และปรับสเกลให้เป็น True Eye) เหนือหัวผู้เล่น
                    int createdIdx = NPC.NewNPC(
                        NPC.GetBossSpawnSource(i),
                        (int)p.Center.X,
                        (int)(p.Center.Y - 120f),
                        NPCID.ServantofCthulhu,
                        0,
                        i,
                        1f,
                        0f,
                        0f
                    );

                    if (createdIdx >= 0 && createdIdx < Main.maxNPCs)
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

                if (eyeIdx >= 0 && eyeIdx < Main.maxNPCs && Main.npc[eyeIdx].active)
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

                    // นับรอบทุกๆ 10 วินาที (600 ticks) เพื่อเสก Phantasmal Sphere
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
                NPC.GetBossSpawnSource(targetPlayerIndex),
                spawnPos,
                Vector2.Zero,
                ProjectileID.PhantasmalSphere,
                32,
                0f,
                Main.myPlayer
            );

            if (projIdx >= 0 && projIdx < Main.maxProjectiles)
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

                TShock.Players[targetPlayerIndex]?.SendWarningMessage("True Eye เตรียมยิง Phantasmal Sphere ใน 5 วินาที!");
            }
        }

        private void UpdatePhantasmalSphereAttacks()
        {
            const int fiveSecondsWarningTicks = 300; // 60 ticks/sec * 5 sec
            const float shootSpeed = 16.5f;

            List<SphereAttackState> remaining = new List<SphereAttackState>();

            foreach (var s in _state.ActiveSpheres)
            {
                if (s.ProjectileIndex < 0 || s.ProjectileIndex >= Main.maxProjectiles)
                    continue;

                Projectile proj = Main.projectile[s.ProjectileIndex];
                if (!proj.active || proj.type != ProjectileID.PhantasmalSphere)
                    continue;

                Player target = (s.TargetPlayerIndex >= 0 && s.TargetPlayerIndex < Main.maxPlayers)
                    ? Main.player[s.TargetPlayerIndex]
                    : null;

                if (!s.HasFired)
                {
                    s.TelegraphTicks++;

                    // ช่วงเตือน 5 วินาที: ลอยค้างเหนือหัว/หน้า True Eye เพื่อเตือนผู้เล่นและอัปเดตทิศเล็ง
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

                    // ครบ 5 วินาที -> ยิงพุ่งใส่ผู้เล่น
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
                    // หลังยิงแล้ว ให้พุ่งต่อเนื่องไปตามทิศที่เล็งไว้
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
                if (i == _state.BossNpcIndex || IsManagedEye(i))
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
                    TSPlayer.All.SendData(PacketTypes.PlayerUpdate, "", i);
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
                    LockHorizontalMovementForPlayers(true);
                    boss.dontTakeDamage = true;
                    boss.alpha = 255;
                    boss.velocity = Vector2.Zero;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

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

                        boss.alpha = 0;
                        boss.dontTakeDamage = false;
                        boss.noTileCollide = true;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                        _state.Stage = UltStage.UfoDash10s;
                        _state.StageTicks = 0;
                    }
                    break;
                }

                case UltStage.UfoDash10s:
                {
                    boss.dontTakeDamage = false;
                    boss.alpha = 0;
                    boss.noTileCollide = true;

                    Vector2 toTarget = groupCenter - boss.Center;
                    if (toTarget.Length() > 1f)
                    {
                        Vector2 dir = Vector2.Normalize(toTarget);
                        float ufoSpeed = 20f;
                        boss.velocity = Vector2.Lerp(boss.velocity, dir * ufoSpeed, 0.14f);
                        boss.direction = dir.X >= 0f ? 1 : -1;
                        boss.spriteDirection = boss.direction;
                    }

                    boss.ai[0] = 3f;
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", boss.whoAmI);

                    if (_state.StageTicks >= 600)
                    {
                        _state.Stage = UltStage.RapidDash1Dmg5s;
                        _state.StageTicks = 0;
                    }
                    break;
                }

                case UltStage.RapidDash1Dmg5s:
                {
                    boss.dontTakeDamage = false;
                    boss.alpha = 0;
                    boss.noTileCollide = true;
                    boss.damage = 1;

                    Vector2 toTarget = groupCenter - boss.Center;
                    if (toTarget.Length() > 1f)
                    {
                        Vector2 dir = Vector2.Normalize(toTarget);
                        float dashSpeed = 28f;
                        boss.velocity = Vector2.Lerp(boss.velocity, dir * dashSpeed, 0.25f);
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
                        LockHorizontalMovementForPlayers(false);
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

        private void SpawnClonesAroundPlayers(Vector2 center, int count)
        {
            CleanupClonesAndEyes();
            for (int i = 0; i < count; i++)
            {
                double angle = (Math.PI * 2.0 / count) * i;
                int x = (int)(center.X + Math.Cos(angle) * 240.0);
                int y = (int)(center.Y + Math.Sin(angle) * 180.0);

                int idx = NPC.NewNPC(NPC.GetBossSpawnSource(-1), x, y, NPCID.ServantofCthulhu, 0);
                if (idx >= 0 && idx < Main.maxNPCs)
                {
                    NPC clone = Main.npc[idx];
                    clone.lifeMax = 500;
                    clone.life = 500;
                    clone.damage = 25;
                    clone.noTileCollide = true;
                    _state.CloneIndices.Add(idx);
                    TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                }
            }
        }

        private void FireCloneLasersAlongFacing(Vector2 targetCenter)
        {
            if (_state.StageTicks % 25 != 0)
                return;

            foreach (int idx in _state.CloneIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    NPC clone = Main.npc[idx];
                    Vector2 dir = targetCenter - clone.Center;
                    if (dir.Length() > 0.01f)
                    {
                        dir.Normalize();
                        FireSingleLaserFromClone(clone, dir);
                    }
                }
            }
        }

        private void FireSingleLaserFromClone(NPC clone, Vector2 dir)
        {
            int projIdx = Projectile.NewProjectile(
                NPC.GetBossSpawnSource(-1),
                clone.Center,
                dir * 14f,
                ProjectileID.DeathLaser,
                22,
                0f,
                Main.myPlayer
            );

            if (projIdx >= 0 && projIdx < Main.maxProjectiles)
            {
                Main.projectile[projIdx].hostile = true;
                Main.projectile[projIdx].friendly = false;
                TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", projIdx);
            }
        }

        private void KeepOnlyTopRightClone(Vector2 groupCenter)
        {
            int bestIdx = -1;
            float bestScore = float.NegativeInfinity;

            foreach (int idx in _state.CloneIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    Vector2 rel = Main.npc[idx].Center - groupCenter;
                    float score = rel.X - rel.Y;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = idx;
                    }
                }
            }

            List<int> kept = new List<int>();
            foreach (int idx in _state.CloneIndices)
            {
                if (idx >= 0 && idx < Main.maxNPCs && Main.npc[idx].active)
                {
                    if (idx == bestIdx)
                    {
                        kept.Add(idx);
                    }
                    else
                    {
                        Main.npc[idx].active = false;
                        TSPlayer.All.SendData(PacketTypes.NpcUpdate, "", idx);
                    }
                }
            }
            _state.CloneIndices = kept;
        }
    }
}
