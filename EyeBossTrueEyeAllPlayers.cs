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
    public class EyeBossTrueEyeAllPlayersPlugin : TerrariaPlugin
    {
        public override string Name => "EyeBossTrueEyeAllPlayers";
        public override string Author => "Shokuya";
        public override string Description => "Phase 2 heals to 100%, True Eye tracks all players, 10% HP ultimate with clones & 10s UFO chase, minecart & clone laser one-shot kills, no normal mobs while boss active, then 5s rapid 1-damage dashes before surrendering idle in front of players";
        public override Version Version => new Version(2, 3, 0);

        private const int SphereCount = 1;
        private const int TicksPerSecond = 60;
        private const int CycleLengthTicks = 10 * TicksPerSecond;          // 10 วินาที
        private const int TelegraphDurationTicks = 3 * TicksPerSecond;      // เตือนลูกบอล 3 วินาที

        // ไทม์ไลน์ช่วงท่าไม้ตาย (หน่วย: เฟรม, 60 เฟรม = 1 วินาที)
        private const int Stage1_HideBossTicks = 1 * TicksPerSecond;          // 0 - 1s: บอสจริงหายตัว
        private const int Stage2_DualChargeTicks = 3 * TicksPerSecond;        // 1 - 4s: 2 โคลนชาร์จเลเซอร์ 3 วิ
        private const int Stage3_DualFireHoldTicks = 2 * TicksPerSecond;      // 4 - 6s: 2 โคลนยิงเลเซอร์ค้าง 2 วิ
        private const int Stage4_SingleTopRightChargeTicks = 3 * TicksPerSecond;// 6 - 9s: เหลือโคลนเดียวอยู่ขวาบน ชาร์จ 3 วิ
        private const int Stage5_InnerLaserAndWaitTicks = 3 * TicksPerSecond;   // 9 - 12s: ยิงจากด้านใน แล้วจับเวลา 3 วิ
        private const int Stage6_UfoChaseTicks = 10 * TicksPerSecond;         // 12 - 22s: ไล่ผู้เล่นแบบ UFO นาน 10 วินาที ไม่ยิงกระสุนเพิ่ม

        // ช่วงหลังจบท่าไม้ตายทั้งหมด: พุ่งรัวดาเมจ 1 นาน 5 วินาที -> ยอมแพ้นิ่งตรงหน้าผู้เล่น
        private const int PostUltimateRapidDashTicks = 5 * TicksPerSecond;    // พุ่งใส่รัวๆ ~5 วินาที
        private const int RapidDashIntervalTicks = 18;                      // พุ่งถี่ทุก ~0.3 วินาที
        private const float RapidDashSpeed = 16.5f;

        // ความเร็วระดับรองเท้าวิ่งเร็วระดับสูง
        private const float HighSpeedShoeChaseSpeed = 13.5f;

        private class BossPhaseState
        {
            public bool IsPhase2 = false;
            public bool HasRevivedToFullHealth = false;
            public bool HasUsedUltimateAt10Percent = false;
            public bool IsHiddenForUltimate = false;
            public bool IsInRapidDashAfterUltimate = false;
            public bool HasSurrendered = false;
            public int RapidDashTimerTicks = 0;
            public int DashPulseCooldown = 0;
        }

        private class PlayerEyeState
        {
            public int EyeNpcIndex = -1;
            public int CycleTicks = 0;
            public int TelegraphProjIndex = -1;
            public bool HasLaunchedThisCycle = false;
        }

        private readonly Dictionary<int, BossPhaseState> _bossStates = new Dictionary<int, BossPhaseState>();
        private readonly Dictionary<int, PlayerEyeState> _playerEyes = new Dictionary<int, PlayerEyeState>();

        private bool _wasPhase2Active = false;
        private int _globalTick = 0;

        // สถานะเฟสท่าไม้ตาย
        private bool _isUltimatePhaseActive = false;
        private int _ultimateTimerTicks = 0;
        private Vector2 _ultimateGatherPoint = Vector2.Zero;
        private float _groundStandY = 0f;

        private int _topRightCloneIndex = -1;
        private int _bottomLeftCloneIndex = -1;
        private int _soloHunterCloneIndex = -1;

        private bool _dualClonesSpawned = false;
        private bool _dualLasersFired = false;
        private bool _soloClonePrepared = false;
        private bool _innerLaserFired = false;
        private bool _chaseStarted = false;

        private readonly List<int> _activeLaserProjIndices = new List<int>();
        private readonly HashSet<int> _deadByLaserThisTick = new HashSet<int>();
        private readonly HashSet<int> _hitByRapidDashOneDamageThisTick = new HashSet<int>();

        public EyeBossTrueEyeAllPlayersPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                CleanupAll();
            }
            base.Dispose(disposing);
        }

        private void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            if (!HasAnyEyeBossInMap())
                return;

            if (args.NpcId < 0 || args.NpcId >= Main.maxNPCs)
                return;

            NPC spawned = Main.npc[args.NpcId];
            if (spawned == null || !spawned.active)
                return;

            if (IsNormalMonster(spawned))
            {
                args.Handled = true;
                spawned.active = false;
                NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, args.NpcId);
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            _globalTick++;
            _hitByRapidDashOneDamageThisTick.Clear();

            bool anyEyeBossInMap = HasAnyEyeBossInMap();

            if (anyEyeBossInMap)
            {
                SuppressNormalMonstersInMap();
            }

            CheckMinecartInstantDeath();

            bool triggerUltimateNow = false;
            NPC triggeringBoss = null;
            bool hasActiveBossSequence = UpdateBossesAndCheckPhase2(out triggerUltimateNow, out triggeringBoss);

            if (!hasActiveBossSequence)
            {
                if (_wasPhase2Active || _playerEyes.Count > 0 || _isUltimatePhaseActive)
                {
                    CleanupAll();
                    _wasPhase2Active = false;
                }
                return;
            }

            if (!_wasPhase2Active)
            {
                _wasPhase2Active = true;
                TSPlayer.All.SendWarningMessage("Eye of Cthulhu เข้าเฟส 2! เลือดกลับมาเต็ม 100% แล้ว True Eye ตามติดผู้เล่นทุกคน >:(");
            }

            if (triggerUltimateNow)
            {
                StartUltimatePhase(triggeringBoss);
            }

            if (_isUltimatePhaseActive)
            {
                UpdateUltimateSequence();
            }

            // อัปเดตพฤติกรรมหลังจบท่าไม้ตาย (พุ่งรัวดาเมจ 1 นาน ~5 วิ -> ยอมแพ้นิ่งตรงหน้าผู้เล่น)
            UpdatePostUltimateBossBehaviors();

            CheckCloneLaserInstantKill();

            bool anyBossSurrendered = HasAnySurrenderedBoss();

            // ตรวจผู้เล่นทุกคนในแมพ
            for (int pIdx = 0; pIdx < Main.maxPlayers; pIdx++)
            {
                Player player = Main.player[pIdx];
                bool isValidPlayer = player != null && player.active && !player.dead;

                if (isValidPlayer)
                {
                    if (!_playerEyes.TryGetValue(pIdx, out PlayerEyeState state))
                    {
                        state = new PlayerEyeState();
                        _playerEyes[pIdx] = state;
                    }

                    // ถ้าบอสยอมแพ้แล้วหรืออยู่ในเฟสไม้ตาย ให้หยุดเสก/ยิงลูกบอล Phantasmal Sphere
                    if (_isUltimatePhaseActive || anyBossSurrendered)
                    {
                        ClearTelegraphSphere(state);
                        if (_isUltimatePhaseActive)
                        {
                            KeepTrueEyeHovering(pIdx, player, state);
                        }
                        else
                        {
                            CleanupPlayerEye(pIdx);
                        }
                    }
                    else
                    {
                        UpdatePlayerEye(pIdx, player, state);
                    }
                }
                else if (_playerEyes.ContainsKey(pIdx))
                {
                    CleanupPlayerEye(pIdx);
                }
            }
        }

        private bool HasAnySurrenderedBoss()
        {
            foreach (KeyValuePair<int, BossPhaseState> kv in _bossStates)
            {
                if (kv.Value.HasSurrendered)
                    return true;
            }
            return false;
        }

        private void UpdatePostUltimateBossBehaviors()
        {
            foreach (KeyValuePair<int, BossPhaseState> kv in _bossStates)
            {
                int idx = kv.Key;
                BossPhaseState state = kv.Value;

                if (idx < 0 || idx >= Main.maxNPCs)
                    continue;

                NPC boss = Main.npc[idx];
                if (boss == null || !boss.active || boss.type != NPCID.EyeofCthulhu || boss.life <= 0)
                    continue;

                if (state.IsInRapidDashAfterUltimate)
                {
                    state.RapidDashTimerTicks++;
                    boss.damage = 1;
                    boss.dontTakeDamage = false;

                    Player target = FindNearestAlivePlayer(boss.Center);
                    if (target != null)
                    {
                        state.DashPulseCooldown--;
                        if (state.DashPulseCooldown <= 0)
                        {
                            state.DashPulseCooldown = RapidDashIntervalTicks;
                            Vector2 dir = target.Center - boss.Center;
                            if (dir == Vector2.Zero) dir = new Vector2(1f, 0f);
                            dir.Normalize();
                            boss.velocity = dir * RapidDashSpeed;
                            boss.rotation = (float)Math.Atan2(boss.velocity.Y, boss.velocity.X) - MathHelper.PiOver2;
                            boss.direction = boss.velocity.X >= 0f ? 1 : -1;
                            boss.spriteDirection = boss.direction;
                            boss.netUpdate = true;
                        }

                        // ให้ดาเมจจากการพุ่งชนเพียงแค่ 1 เท่านั้น
                        ApplyExactOneDamageOnTouch(boss);
                    }

                    if (_globalTick % 2 == 0)
                    {
                        NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, idx);
                    }

                    if (state.RapidDashTimerTicks >= PostUltimateRapidDashTicks)
                    {
                        state.IsInRapidDashAfterUltimate = false;
                        state.HasSurrendered = true;
                        boss.velocity = Vector2.Zero;
                        boss.damage = 0;
                        boss.dontTakeDamage = true;
                        boss.netUpdate = true;
                        NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, idx);
                        TSPlayer.All.SendInfoMessage($"{boss.FullName} พุ่งครบ 5 วินาทีแล้วและยอมแพ้! อยู่ตรงหน้าผู้เล่นและไม่ทำอะไรเลย :3");
                    }
                }
                else if (state.HasSurrendered)
                {
                    // บอสยอมแพ้: อยู่ตรงหน้าผู้เล่นและไม่ทำอะไรผู้เล่น / ไม่ทำอะไรเลย
                    boss.damage = 0;
                    boss.dontTakeDamage = true;
                    boss.velocity = Vector2.Zero;

                    Player target = FindNearestAlivePlayer(boss.Center);
                    if (target != null)
                    {
                        int faceDir = target.Center.X >= boss.Center.X ? 1 : -1;
                        Vector2 inFrontOfPlayer = target.Center + new Vector2(faceDir * 95f, -16f);
                        boss.Center = inFrontOfPlayer;
                        boss.direction = -faceDir;
                        boss.spriteDirection = -faceDir;
                        boss.rotation = faceDir > 0 ? MathHelper.Pi : 0f;
                    }

                    if (_globalTick % 4 == 0)
                    {
                        NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, idx);
                    }
                }
            }
        }

        private void ApplyExactOneDamageOnTouch(NPC boss)
        {
            float hitRange = Math.Max(boss.width, boss.height) * 0.65f;

            for (int pIdx = 0; pIdx < Main.maxPlayers; pIdx++)
            {
                if (_hitByRapidDashOneDamageThisTick.Contains(pIdx))
                    continue;

                Player p = Main.player[pIdx];
                if (p == null || !p.active || p.dead)
                    continue;

                if (Vector2.Distance(boss.Center, p.Center) <= hitRange)
                {
                    _hitByRapidDashOneDamageThisTick.Add(pIdx);
                    TSPlayer tsPlayer = TShock.Players[pIdx];
                    if (tsPlayer != null && tsPlayer.ConnectionAlive)
                    {
                        tsPlayer.DamagePlayer(1);
                    }
                    else if (p.statLife > 1)
                    {
                        p.statLife -= 1;
                        NetMessage.SendData((int)PacketTypes.PlayerHP, -1, -1, null, pIdx);
                    }
                }
            }
        }

        private bool HasAnyEyeBossInMap()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc != null && npc.active && npc.type == NPCID.EyeofCthulhu && npc.life > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsNormalMonster(NPC npc)
        {
            if (npc == null || !npc.active)
                return false;

            if (npc.boss || npc.townNPC || npc.friendly)
                return false;

            if (npc.type == NPCID.MoonLordFreeEye || npc.type == NPCID.EyeofCthulhu)
                return false;

            // ยกเว้นลูกน้องของบอสให้ยังอยู่ได้
            if (npc.type == NPCID.ServantofCthulhu)
                return false;

            return true;
        }

        private void SuppressNormalMonstersInMap()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (IsNormalMonster(npc))
                {
                    npc.active = false;
                    NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, i);
                }
            }
        }

        private void CheckCloneLaserInstantKill()
        {
            _deadByLaserThisTick.Clear();

            for (int i = 0; i < _activeLaserProjIndices.Count; i++)
            {
                int pIdx = _activeLaserProjIndices[i];
                if (pIdx < 0 || pIdx >= Main.maxProjectiles)
                    continue;

                Projectile laser = Main.projectile[pIdx];
                if (!laser.active || laser.type != ProjectileID.PhantasmalDeathray)
                    continue;

                Vector2 start = laser.Center;
                float angle = laser.ai[1];
                Vector2 dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                const float maxBeamLength = 2400f;
                const float hitRadius = 38f;
                Vector2 end = start + dir * maxBeamLength;

                for (int plIdx = 0; plIdx < Main.maxPlayers; plIdx++)
                {
                    if (_deadByLaserThisTick.Contains(plIdx))
                        continue;

                    Player player = Main.player[plIdx];
                    if (player == null || !player.active || player.dead)
                        continue;

                    float distToSegment = DistancePointToSegment(player.Center, start, end);
                    if (distToSegment <= hitRadius + Math.Max(player.width, player.height) * 0.4f)
                    {
                        _deadByLaserThisTick.Add(plIdx);
                        KillPlayerImmediately(plIdx, "โดนเลเซอร์ร่างโคลนของ Eye of Cthulhu ยิงทีเดียวตายทันที! >:(");
                    }
                }
            }
        }

        private float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq <= 0.0001f)
                return Vector2.Distance(p, a);

            float t = Vector2.Dot(p - a, ab) / lenSq;
            t = MathHelper.Clamp(t, 0f, 1f);
            Vector2 closest = a + ab * t;
            return Vector2.Distance(p, closest);
        }

        private void KillPlayerImmediately(int playerIndex, string reasonMessage)
        {
            Player player = Main.player[playerIndex];
            if (player == null || !player.active || player.dead)
                return;

            TSPlayer tsPlayer = TShock.Players[playerIndex];
            if (tsPlayer != null && tsPlayer.ConnectionAlive)
            {
                tsPlayer.KillPlayer();
                tsPlayer.SendErrorMessage(reasonMessage);
            }
            else
            {
                player.KillMe(PlayerDeathReason.LegacyDefault(), 999999, 0, false);
                NetMessage.SendPlayerDeath(playerIndex, PlayerDeathReason.LegacyDefault(), 999999, 0, false, -1, -1);
            }
        }

        private void CheckMinecartInstantDeath()
        {
            for (int pIdx = 0; pIdx < Main.maxPlayers; pIdx++)
            {
                Player player = Main.player[pIdx];
                if (player == null || !player.active || player.dead)
                    continue;

                if (player.mount != null && player.mount.Active && player.mount.Cart)
                {
                    player.mount.Dismount(player);
                    NetMessage.SendData((int)PacketTypes.PlayerControls, -1, -1, null, pIdx);
                    KillPlayerImmediately(pIdx, "ห้ามใช้รถราง! คุณตายทันที >:(");
                }
            }
        }

        private void StartUltimatePhase(NPC triggeringBoss)
        {
            RemoveAllPhantasmalSpheres();

            Vector2 sumPos = Vector2.Zero;
            int aliveCount = 0;
            float maxBottomY = float.MinValue;

            for (int pIdx = 0; pIdx < Main.maxPlayers; pIdx++)
            {
                Player p = Main.player[pIdx];
                if (p != null && p.active && !p.dead)
                {
                    sumPos += p.position;
                    aliveCount++;
                    float standY = FindGroundStandingY(p);
                    if (standY > maxBottomY)
                        maxBottomY = standY;
                }
            }

            if (aliveCount > 0)
            {
                _ultimateGatherPoint = sumPos / aliveCount;
                _groundStandY = maxBottomY > float.MinValue ? maxBottomY : (_ultimateGatherPoint.Y + 42f);
            }
            else if (triggeringBoss != null && triggeringBoss.active)
            {
                _ultimateGatherPoint = triggeringBoss.position;
                _groundStandY = _ultimateGatherPoint.Y + 80f;
            }

            for (int pIdx = 0; pIdx < Main.maxPlayers; pIdx++)
            {
                Player p = Main.player[pIdx];
                if (p != null && p.active && !p.dead)
                {
                    p.position = _ultimateGatherPoint;
                    p.velocity.X = 0f;
                    NetMessage.SendData((int)PacketTypes.PlayerControls, -1, -1, null, pIdx);
                }
            }

            HideOriginalBosses(true);

            _isUltimatePhaseActive = true;
            _ultimateTimerTicks = 0;
            _dualClo
