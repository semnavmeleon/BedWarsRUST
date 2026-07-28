using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Ludo_Rooms", "Semnavmeleon", "0.2.0")]
    [Description("Runs several concurrent BedWars-style matches on the same physical arena at identical world coordinates. All rooms get network visibility isolation (CanNetworkTo + OnNetworkSubscribersLeave, unbounded). Only room #1 additionally gets true movement collision isolation (Physics.IgnoreLayerCollision, since Physics.IgnoreCollision does not affect Rust's player movement) - this needs 2 dedicated Unity layers (structures + that room's players) and only 3 free layers exist on this server, so it cannot be extended to more than one room without deeper engine work.")]
    public class Ludo_Rooms : RustPlugin
    {
        #region Config

        private Configuration _config;

        public class Configuration
        {
            public string AdminPermission = "ludorooms.admin";

            [JsonProperty("Max concurrent rooms - confirmed via /rooms layers that only 3 completely free Unity layer slots exist on this server, so rooms beyond 3 get visibility isolation only (no collision isolation)")]
            public int MaxConcurrentRooms = 3;

            [JsonProperty("Default scan radius (m) if not given on /rooms scan")]
            public float DefaultScanRadius = 60f;

            [JsonProperty("Marker cross-ref radius (0 = use the scan radius)")]
            public float MarkerCrossRefRadius = 0f;

            [JsonProperty("Prefab path substrings to exclude from a scan by default (case-insensitive)")]
            public List<string> ExcludePrefabSubstrings = new List<string>
            {
                "tree", "cliff", "rock_formation", "junkpile", "item_drop", "player_corpse", "npc_corpse"
            };

            [JsonProperty("Hide every room's content from a player not currently assigned to any room")]
            public bool HideRoomsFromUnassignedPlayers = true;

            [JsonProperty("Give room #1 full collision isolation (not just visibility) by moving it and its players to dedicated Unity layers. Only 3 free layers exist on this server, so only ONE room can get this - rooms 2+ are visibility-isolated only. UNVERIFIED against other Rust systems (hit detection, NPC/turret targeting) since it moves players off their standard layer - disable if it causes side effects")]
            public bool EnableCollisionIsolation = true;

            [JsonProperty("Batch size for bulk spawn/kill/resubscribe operations (avoids tick stalls - see Ludo_Musika's 9-23s stall history)")]
            public int BatchSize = 15;

            [JsonProperty("Delay between batches (seconds)")]
            public float BatchDelaySeconds = 0.05f;

            [JsonProperty("Pre-game lobby countdown length (seconds) - starts once a room has >=1 player with a team assigned, sweeps everyone with a team in at 0")]
            public int LobbyCountdownSeconds = 60;

            [JsonProperty("Respawn cooldown after death (seconds) - server truth, shown live on the scoreboard as 'КД: Ns'")]
            public int RespawnCooldownSeconds = 30;

            [JsonProperty("Match duration (seconds) before it's force-ended regardless of win condition - default 2 hours")]
            public int MatchDurationSeconds = 7200;

            [JsonProperty("Match tick interval (seconds) - drives the countdown, cooldown/win-condition checks, and the scoreboard/countdown CUI refresh. Keep at 1")]
            public float MatchTickSeconds = 1f;

            [JsonProperty("Max distance (m) an admin can stand from a template entity for /rooms bed add to match it as that team's bed")]
            public float BedMatchRadius = 3f;
        }

        [PluginReference] private Plugin ImageLibrary;

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
            }
            catch
            {
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Data model

        private class TemplateEntity
        {
            public string Prefab;
            public float PX, PY, PZ;
            public float RX, RY, RZ, RW;

            public Vector3 Pos => new Vector3(PX, PY, PZ);
            public Quaternion Rot => new Quaternion(RX, RY, RZ, RW);

            public static TemplateEntity From(BaseEntity ent) => new TemplateEntity
            {
                Prefab = ent.PrefabName,
                PX = ent.transform.position.x, PY = ent.transform.position.y, PZ = ent.transform.position.z,
                RX = ent.transform.rotation.x, RY = ent.transform.rotation.y, RZ = ent.transform.rotation.z, RW = ent.transform.rotation.w
            };
        }

        private class TemplateMarker
        {
            public int MarkerId;
            public string DisplayText;
            public string Color; // raw "r g b[ a]" straight from Ludo_Markers' MarkerColors, null if unset
            public float X, Y, Z;
            public Vector3 Position => new Vector3(X, Y, Z);
        }

        // Single point, no team - reused both for a template's pre-game/death lobby point and for
        // the single server-wide main lobby (StoredData.MainLobbyPoint). Kept as its own tiny type
        // rather than reusing SpawnPoint so "no team yet" never needs a null/empty-string special case.
        private class LobbyPointDto
        {
            public float X, Y, Z;
            public Vector3 Position => new Vector3(X, Y, Z);
        }

        // One bed per team per template, registered by an admin standing at/near it (same "stand
        // there, run the command" idiom as SpawnPoint/Ludo_Markers). Stores the POSITION of the
        // nearest Template.Entities entry at registration time rather than an index into that list -
        // SpawnBatch's spec index and _roomEntities' append index only agree when every
        // CreateEntity() call in a batch succeeds, so index-based identity could silently desync.
        // Re-matching by position against whatever's actually alive in the room (LinkBedsForRoom) is
        // robust to that, and is also what makes bed-alive state reload-safe.
        private class BedRegistration
        {
            public int Id;
            public string Team; // free text, matched case-insensitively - same convention as SpawnPoint.Team
            public float X, Y, Z;
            public Vector3 Position => new Vector3(X, Y, Z);
        }

        // Dedicated respawn points, placed the same way Ludo_Markers places its team-name labels
        // (admin stands at the exact spot, runs a command, position is saved) - kept separate from
        // TeamMarkers/Ludo_Markers cross-referencing because a marker's label position (convenient
        // for a floating ESP text) isn't necessarily the exact tactical spot a player should be
        // teleported to on respawn (e.g. right at a bed, not floating over it).
        private class SpawnPoint
        {
            public int Id;
            public string Team; // free text - "red", "yellow", "blue", "green", matched case-insensitively
            public float X, Y, Z;
            public Vector3 Position => new Vector3(X, Y, Z);
        }

        private class Template
        {
            public string Name;
            public float ScanX, ScanY, ScanZ, ScanRadius;
            public string ScannedAt;
            public List<TemplateEntity> Entities = new List<TemplateEntity>();
            public List<TemplateMarker> TeamMarkers = new List<TemplateMarker>();
            public List<SpawnPoint> SpawnPoints = new List<SpawnPoint>();
            public int NextSpawnPointId = 1;
            public List<BedRegistration> Beds = new List<BedRegistration>();
            public int NextBedId = 1;
            public LobbyPointDto LobbyPoint; // null until /rooms lobby set <template>
        }

        private class RoomSlot
        {
            public int SlotId;
            public bool InUse;
            public string TemplateName;
            public List<ulong> Players = new List<ulong>();
        }

        private class StoredData
        {
            public Dictionary<string, Template> Templates = new Dictionary<string, Template>();
            public List<RoomSlot> Slots = new List<RoomSlot>();
            public LobbyPointDto MainLobbyPoint; // server-wide, not per-template - set via /rooms mainlobby set
        }

        private StoredData _data;

        private void LoadData() => _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _data);

        #endregion

        #region Runtime state

        // Keyed by entity reference, not net.ID - net.ID isn't allocated until Spawn() runs, and
        // tagging must happen before Spawn() so the very first CanNetworkTo check is already
        // correct (tagging after Spawn() leaks one frame of visibility to everyone - confirmed in
        // Ludo_RoomsSpike). SlotId 0 is never a valid room, used as a sentinel nowhere here.
        private readonly Dictionary<BaseEntity, int> _entityRoom = new Dictionary<BaseEntity, int>();
        private readonly Dictionary<ulong, int> _playerRoom = new Dictionary<ulong, int>();
        private readonly Dictionary<int, List<BaseEntity>> _roomEntities = new Dictionary<int, List<BaseEntity>>();

        // Live-built entities (player-placed during a match) are tagged separately from
        // _entityRoom - their OwnerID is meaningful (Tool Cupboard auth, decay ownership) and must
        // not be overwritten, so room membership lives in this side-table instead, keyed by net.ID
        // (valid here since these are already-spawned entities by the time OnEntityBuilt fires).
        private readonly Dictionary<NetworkableId, int> _liveBuiltRoomOf = new Dictionary<NetworkableId, int>();

        private const ulong RoomOwnerIdBase = 999999999300000000UL; // distinct range from Ludo_Musika's (...001) and Ludo_RoomsSpike's (...900000001)

        #endregion

        #region Match state (lobby/countdown/live, per-player team+alive+cooldown, bed tracking)

        private enum MatchPhase { Lobby, Countdown, Live }

        private class PlayerMatchState
        {
            public string Team;
            public bool Alive = true;
            public bool Eliminated = false;
            public int CooldownRemaining; // seconds, meaningful only while !Alive && !Eliminated
        }

        // In-memory only, like _playerRoom - resets to a clean Lobby phase on every plugin reload
        // (see ReclaimOrphans). Team/alive/cooldown state isn't worth persisting across a reload
        // for how rarely that happens vs. the RoomSlot.Players refactor it would take.
        private class RoomMatchState
        {
            public MatchPhase Phase = MatchPhase.Lobby;
            public int SecondsRemaining; // countdown-only
            public readonly Dictionary<ulong, PlayerMatchState> Players = new Dictionary<ulong, PlayerMatchState>();
            public readonly Dictionary<string, bool> BedAlive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, BaseEntity> BedEntity = new Dictionary<string, BaseEntity>(StringComparer.OrdinalIgnoreCase);
            public Timer RoomTickTimer;      // 1Hz, drives both Countdown and Live
            public Timer MatchDurationTimer; // one-shot, started at GoLive
        }

        private readonly Dictionary<int, RoomMatchState> _matchState = new Dictionary<int, RoomMatchState>();

        // Reverse lookup for OnEntityKill - which room/team does this exact spawned entity belong
        // to as a bed. Same "keyed by entity reference" idiom as _entityRoom.
        private readonly Dictionary<BaseEntity, (int slotId, string team)> _bedEntityLookup = new Dictionary<BaseEntity, (int, string)>();

        // Dedupes the Steam avatar-scrape webrequest per userid - GetImage() never null/empty-
        // returns (see EnsureAvatar), so this guard is the only thing stopping a re-fire every 1Hz
        // refresh tick while the async fetch is still in flight.
        private readonly HashSet<ulong> _avatarRequested = new HashSet<ulong>();

        #endregion

        #region Lifecycle

        private bool HasPermission(BasePlayer player) => player != null && permission.UserHasPermission(player.UserIDString, _config.AdminPermission);

        void Init()
        {
            permission.RegisterPermission(_config.AdminPermission, this);
            LoadData();

            bool addedSlots = false;
            for (int i = 1; i <= _config.MaxConcurrentRooms; i++)
                if (_data.Slots.All(s => s.SlotId != i))
                {
                    _data.Slots.Add(new RoomSlot { SlotId = i, InUse = false });
                    addedSlots = true;
                }
            if (addedSlots) SaveData();

            ReclaimOrphans();
            _visibilityUpkeepTimer = timer.Every(2f, VisibilityUpkeepTick);

            // Also runs periodically, not just at reload - OnEntityBuilt only tags an entity if the
            // builder was already room-assigned at the exact moment they placed it, so anything
            // built before /rooms join (or missed by that hook for any other reason) would
            // otherwise never get picked up until the next reload. Same self-healing pattern as the
            // structure layer/visibility/flyhack upkeep ticks above - heavier (does a Vis.Entities
            // scan per active room), so on a slower interval than those.
            _liveBuiltReclaimTimer = timer.Every(15f, ReclaimLiveBuiltEntities);
        }

        // Reclaim (not kill) anything left over from before a plugin-only reload, mirroring
        // Ludo_Musika's RemoveOrphanedSpawns precedent, but "adopt back" for slots the persisted
        // data says are still InUse, and only kill genuinely orphaned entities (slot not InUse -
        // e.g. after a crash where teardown never ran).
        private void ReclaimOrphans()
        {
            var inUseSlots = new HashSet<int>(_data.Slots.Where(s => s.InUse).Select(s => s.SlotId));
            var toKill = new List<BaseEntity>();

            // _collisionIsolationReady is in-memory and resets on every reload, so the ignore-
            // matrix entry needs re-establishing for the fully-isolated room if it's still active,
            // even though the matrix entry itself likely survives a plugin-only reload
            // independently (PhysX state isn't tied to the managed-code lifecycle) - cheap and
            // idempotent either way.
            foreach (var slotId in inUseSlots)
                SetupCollisionIsolationForRoom(slotId);

            foreach (var net in BaseNetworkable.serverEntities)
            {
                var ent = net as BaseEntity;
                if (ent == null || ent.IsDestroyed) continue;
                if (ent.OwnerID < RoomOwnerIdBase || ent.OwnerID >= RoomOwnerIdBase + 1000) continue;

                int slotId = (int)(ent.OwnerID - RoomOwnerIdBase);
                if (inUseSlots.Contains(slotId))
                {
                    _entityRoom[ent] = slotId;
                    if (!_roomEntities.TryGetValue(slotId, out var list))
                        _roomEntities[slotId] = list = new List<BaseEntity>();
                    list.Add(ent);
                    ApplyLayerForRoom(ent, slotId);
                }
                else
                {
                    toKill.Add(ent);
                }
            }

            if (toKill.Count > 0)
            {
                Puts($"[Ludo_Rooms] Killing {toKill.Count} orphaned room entity(ies) from a slot no longer marked in-use.");
                KillBatch(toKill, 0);
            }

            // _playerRoom is in-memory only and resets on every reload - reconstruct it from the
            // persisted RoomSlot.Players list for anyone still actually online, and re-apply their
            // collision layer (their GameObject's live layer survives the reload untouched, but
            // without this their _playerRoom entry would be gone, leaving them in an inconsistent
            // state: physically still on the room's player layer, but CanNetworkTo would treat them
            // as unassigned and hide everything from them).
            foreach (var slot in _data.Slots.Where(s => s.InUse))
            {
                foreach (var userId in slot.Players.ToList())
                {
                    var p = BasePlayer.FindByID(userId);
                    if (p == null) { slot.Players.Remove(userId); continue; }
                    _playerRoom[p.userID] = slot.SlotId;
                    ApplyPlayerLayerForRoom(p, slot.SlotId);
                }
            }

            // Match state (team/alive/cooldown/countdown) is in-memory only and does not survive a
            // reload (see the "Match state" region) - every InUse room comes back in a clean Lobby
            // phase, players keep their room membership (reconstructed above) but lose their team
            // and must /rooms join/auto again. Bed-alive state DOES need re-deriving here though,
            // since a bed genuinely destroyed before the reload must not silently come back "alive".
            foreach (var slot in _data.Slots.Where(s => s.InUse))
                LinkBedsForRoom(slot.SlotId);

            ReclaimLiveBuiltEntities();
        }

        // _liveBuiltRoomOf is in-memory only too and does NOT survive a reload, unlike template-
        // spawned entities (found back above via their synthetic RoomOwnerIdBase OwnerID) - a
        // player-built entity deliberately keeps the real player's OwnerID (for Tool Cupboard
        // auth/decay), so there is no tag to find it by directly after a reload. This is exactly
        // why /rooms remove was failing to demolish player-built structures and why they'd
        // silently become visible to everyone (GetEntityRoom returning null once untracked).
        // Reconstructed heuristically instead: for every InUse room with a scanned template, look
        // for entities within that template's own scan radius (bounding the search to the room's
        // actual footprint, so this can't accidentally sweep up a room member's real, unrelated
        // base elsewhere on the map) owned by a player CURRENTLY assigned to that room and not
        // already one of our own template duplicates.
        private void ReclaimLiveBuiltEntities()
        {
            foreach (var slot in _data.Slots.Where(s => s.InUse))
            {
                var template = GetTemplate(slot);
                if (template == null || template.ScanRadius <= 0f || slot.Players.Count == 0) continue;

                var center = new Vector3(template.ScanX, template.ScanY, template.ScanZ);
                var roomPlayerIds = new HashSet<ulong>(slot.Players);

                var buffer = new List<BaseEntity>();
                Vis.Entities(center, template.ScanRadius, buffer);
                foreach (var ent in buffer)
                {
                    if (ent == null || ent.IsDestroyed || ent is BasePlayer || ent.net == null) continue;
                    if (_entityRoom.ContainsKey(ent) || _liveBuiltRoomOf.ContainsKey(ent.net.ID)) continue;
                    if (!roomPlayerIds.Contains(ent.OwnerID)) continue;

                    _liveBuiltRoomOf[ent.net.ID] = slot.SlotId;
                    ApplyLayerForRoom(ent, slot.SlotId);
                }
            }
        }

        void Unload()
        {
            foreach (var state in _matchState.Values)
            {
                state.RoomTickTimer?.Destroy();
                state.MatchDurationTimer?.Destroy();
            }
            _flyHackPauseTimer?.Destroy();
            _visibilityUpkeepTimer?.Destroy();
            _liveBuiltReclaimTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
                DestroyMatchUi(player);
        }

        private void KillBatch(List<BaseEntity> list, int index)
        {
            int processed = 0;
            while (index < list.Count && processed < _config.BatchSize)
            {
                var ent = list[index];
                if (ent != null && !ent.IsDestroyed) ent.Kill();
                index++; processed++;
            }
            if (index < list.Count)
                timer.Once(_config.BatchDelaySeconds, () => KillBatch(list, index));
        }

        #endregion

        #region Visibility (validated in Ludo_RoomsSpike.cs - see plan's Phase 0 results)

        private int? GetEntityRoom(BaseEntity be)
        {
            if (be is BasePlayer bp)
                return _playerRoom.TryGetValue(bp.userID, out var pr) ? pr : (int?)null;
            if (_entityRoom.TryGetValue(be, out var er)) return er;
            if (be.net != null && _liveBuiltRoomOf.TryGetValue(be.net.ID, out var lr)) return lr;
            return null;
        }

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
        {
            if (entity == null || target == null) return null;
            var be = entity as BaseEntity;
            if (be == null || be == target) return null;

            int? entityRoom = GetEntityRoom(be);
            if (entityRoom == null) return null;

            int? targetRoom = _playerRoom.TryGetValue(target.userID, out var r) ? r : (int?)null;
            if (targetRoom == null)
                return _config.HideRoomsFromUnassignedPlayers ? (object)false : null;

            return entityRoom.Value == targetRoom.Value ? null : (object)false;
        }

        // Revoke: proven via Vanish.cs (Whispers88), Disappear() - the only mechanism found that
        // actually updates whatever bookkeeping the engine's real subscription/visibility system
        // consults. A hand-crafted destroy packet and player.net.subscriber.Unsubscribe were both
        // tried and failed (cosmetic-only / wrong bookkeeping - see plan Phase 0 results #3).
        private void HideFrom(BaseEntity entity, BasePlayer viewer)
        {
            if (entity == null || entity.IsDestroyed || viewer?.net?.connection == null) return;
            entity.OnNetworkSubscribersLeave(new List<Connection> { viewer.net.connection });
        }

        // Grant: proven via Trade.cs's StartLoot/SendEntity pattern - needed because a player
        // switching into a room may never have gone through the normal distance-based
        // auto-subscription for this entity at all.
        private void SendEntity(BasePlayer viewer, BaseEntity entity)
        {
            if (!Net.sv.IsConnected() || entity?.net == null || viewer?.net?.connection == null) return;
            var write = Net.sv.StartWrite();
            viewer.net.connection.validate.entityUpdates++;
            var saveInfo = new BaseNetworkable.SaveInfo { forConnection = viewer.net.connection, forDisk = false };
            write.PacketID(Message.Type.Entities);
            write.UInt32(viewer.net.connection.validate.entityUpdates);
            entity.ToStreamForNetwork(write, saveInfo);
            write.Send(new SendInfo(viewer.net.connection));
        }

        private void ApplyPair(BaseEntity entity, int? entityRoom, BasePlayer viewer, int? viewerRoom)
        {
            if (entity == null || entity.IsDestroyed || viewer == null) return;
            bool shouldSee = entityRoom.HasValue && viewerRoom.HasValue && entityRoom.Value == viewerRoom.Value;
            if (shouldSee) SendEntity(viewer, entity);
            else HideFrom(entity, viewer);
        }

        // Applies bidirectionally whenever ONE player's room membership changes: what the
        // switcher can (not) see, and what every other online player can (not) see OF the
        // switcher - both directions matter since BasePlayer is itself room-trackable.
        private void RefreshVisibilityFor(BasePlayer player)
        {
            int? myRoom = _playerRoom.TryGetValue(player.userID, out var r) ? r : (int?)null;

            foreach (var kv in new List<KeyValuePair<BaseEntity, int>>(_entityRoom))
                ApplyPair(kv.Key, kv.Value, player, myRoom);

            foreach (var kv in new List<KeyValuePair<NetworkableId, int>>(_liveBuiltRoomOf))
            {
                var ent = BaseNetworkable.serverEntities.Find(kv.Key) as BaseEntity;
                ApplyPair(ent, kv.Value, player, myRoom);
            }

            foreach (var other in BasePlayer.activePlayerList)
            {
                if (other == null || other == player) continue;
                int? otherRoom = _playerRoom.TryGetValue(other.userID, out var or) ? or : (int?)null;

                ApplyPair(other, otherRoom, player, myRoom);
                ApplyPair(player, myRoom, other, otherRoom);
            }
        }

        // A one-shot RefreshVisibilityFor at /rooms join time is NOT enough on its own: Rust's own
        // network-group system re-adds nearby entities to a client's subscription in bulk (grid/
        // proximity-based) independently of any single CanNetworkTo check at join time, which
        // silently re-shows a cross-room entity sometime later without this plugin ever being asked
        // again - same category of problem as the StructureLayer collider reverting on its own,
        // just for networking instead of physics. Re-asserting HideFrom/SendEntity for every
        // room-assigned player on a short interval self-heals against that regardless of exactly
        // when/how Rust's engine re-adds them, instead of trying to catch every possible trigger.
        private Timer _visibilityUpkeepTimer;
        private Timer _liveBuiltReclaimTimer;

        private void VisibilityUpkeepTick()
        {
            if (_playerRoom.Count == 0) return;
            foreach (var userId in _playerRoom.Keys.ToList())
            {
                var p = BasePlayer.FindByID(userId);
                if (p != null) RefreshVisibilityFor(p);
            }
        }

        #endregion

        #region Collision isolation (Physics.IgnoreLayerCollision + free layers - see plan's Phase 0 results #6-9)

        // CORRECTED DESIGN (previous version was broken): merely moving a room's STRUCTURES to
        // their own layer and ignoring that layer against the standard "Player (Server)" layer
        // makes the room's structures non-solid for EVERY player, including that room's own
        // occupants - there was no differentiation at all, just a global on/off switch. True
        // per-room differentiation requires the room's OWN PLAYERS to also move to a dedicated
        // layer, so the ignore-rule only fires against players who are NOT on that room's player
        // layer. That costs 2 layers per room (1 structure + 1 player), and this server has only
        // 3 completely free layers total (confirmed via /rooms layers: raw indices 3, 6, 7 - no
        // layer is actually named "ReservedN" here). That covers exactly ONE fully isolated room
        // (visibility + collision). Rooms beyond that get visibility isolation only (CanNetworkTo/
        // OnNetworkSubscribersLeave, which has no layer limit) - their structures stay on the
        // default layer, so a player who deliberately walks to that exact spot without being a
        // member would still physically collide with an invisible structure. Ordinary players
        // won't stumble into this by accident since they can't see it there in the first place,
        // and OnEntityTakeDamage still blocks cross-room damage regardless.
        private const int FullyIsolatedRoomSlot = 1;
        private const int StructureLayer = 3;
        private const int RoomPlayerLayer = 6;
        // Layer 7 is left spare/unused for now.

        private int _standardPlayerLayer = -1;
        private bool _collisionIsolationReady;
        private Timer _flyHackPauseTimer;

        private void SetupCollisionIsolationForRoom(int slotId)
        {
            if (!_config.EnableCollisionIsolation || slotId != FullyIsolatedRoomSlot || _collisionIsolationReady) return;

            _standardPlayerLayer = LayerMask.NameToLayer("Player (Server)");
            if (_standardPlayerLayer < 0)
            {
                Puts("[Ludo_Rooms] Could not find the 'Player (Server)' layer - collision isolation disabled, visibility isolation still applies.");
                return;
            }

            Physics.IgnoreLayerCollision(StructureLayer, _standardPlayerLayer, true);
            _collisionIsolationReady = true;

            // Room #1's own players sit on a custom layer that Rust's built-in ground/movement
            // anti-hack almost certainly never accounts for (it's not "Player (Server)"), which in
            // practice showed up as rubber-banding, getting stuck standing on the room's own
            // structures, and outright flyhack kicks at height - confirmed by grepping the actual
            // installed Assembly-CSharp.dll for the real method name: BasePlayer.PauseFlyHackDetection()
            // exists, but no matching ResumeFlyHackDetection does - it reads as a self-expiring
            // suppression rather than a manual on/off pair, so it has to be refreshed periodically
            // for as long as a player is actually on RoomPlayerLayer, not just toggled once on entry.
            //
            // The same timer also continuously reapplies StructureLayer to every room #1 entity -
            // the one-shot ReapplyLayerNextTick (0.2s after spawn) turned out to NOT be enough on
            // its own: a BuildingBlock's collider apparently gets rebuilt more than once and not
            // strictly within the first fraction of a second after spawn (most likely on Rust's own
            // periodic construction-stability recalculation, not just at spawn time), so a single
            // delayed reapply can still lose the race and leave the collider back on the default
            // layer - which is exactly the "phantom wall" a room #2 player walks into even though
            // it's correctly invisible to them. Reapplying every 2s self-heals against that
            // regardless of exactly when/how often it happens, instead of guessing the right delay.
            _flyHackPauseTimer = timer.Every(2f, Room1UpkeepTick);
            Puts($"[Ludo_Rooms] Room #{FullyIsolatedRoomSlot}: full collision isolation ready (structures on layer {StructureLayer}, that room's players move to layer {RoomPlayerLayer} on join).");
        }

        private void Room1UpkeepTick()
        {
            foreach (var kv in _playerRoom)
            {
                if (kv.Value != FullyIsolatedRoomSlot) continue;
                var p = BasePlayer.FindByID(kv.Key);
                p?.PauseFlyHackDetection();
            }

            if (_roomEntities.TryGetValue(FullyIsolatedRoomSlot, out var list))
                foreach (var ent in list)
                    ApplyLayerForRoom(ent, FullyIsolatedRoomSlot);

            foreach (var kv in _liveBuiltRoomOf)
            {
                if (kv.Value != FullyIsolatedRoomSlot) continue;
                var ent = BaseNetworkable.serverEntities.Find(kv.Key) as BaseEntity;
                if (ent != null) ApplyLayerForRoom(ent, FullyIsolatedRoomSlot);
            }
        }

        private void ApplyLayerForRoom(BaseEntity ent, int slotId)
        {
            if (!_config.EnableCollisionIsolation || ent == null || ent.IsDestroyed || slotId != FullyIsolatedRoomSlot || !_collisionIsolationReady) return;
            foreach (var col in ent.GetComponentsInChildren<Collider>(true))
                col.gameObject.layer = StructureLayer;
        }

        // BuildingBlock never gets SetGrade() called on it here (templates don't store grade), and
        // its grade-specific mesh/collider appears to finish setting up on a later tick than
        // deployables' (which have no grade step and are fully solid right after Spawn()) - a
        // collider assigned right after Spawn() can get silently replaced by that later setup,
        // reverting to the default layer and leaving the block invisible (CanNetworkTo still hides
        // it correctly) but still solid to everyone, not just this room's own players. Re-applying
        // once more a couple of ticks later re-catches whatever collider is actually live by then.
        // Cheap and idempotent if the block's collider never changes in the first place.
        private void ReapplyLayerNextTick(BaseEntity ent, int slotId)
        {
            if (!_config.EnableCollisionIsolation || slotId != FullyIsolatedRoomSlot || !(ent is BuildingBlock)) return;
            timer.Once(0.2f, () => ApplyLayerForRoom(ent, slotId));
        }

        // Moves a player's own colliders onto the room's dedicated player layer on join (so they
        // keep colliding normally with their own room's structures, which sit on StructureLayer -
        // untouched by the ignore-rule above, which only targets the STANDARD player layer), and
        // back onto the standard layer on leave. This is the least-tested part of the whole
        // plugin - changing a player's layer could affect other Rust systems that assume the
        // standard value (hit detection, NPC/turret targeting) - watch for side effects.
        private void ApplyPlayerLayerForRoom(BasePlayer player, int? slotId)
        {
            if (!_config.EnableCollisionIsolation || player == null || !_collisionIsolationReady) return;
            int targetLayer = slotId == FullyIsolatedRoomSlot ? RoomPlayerLayer : _standardPlayerLayer;
            foreach (var col in player.GetComponentsInChildren<Collider>(true))
                col.gameObject.layer = targetLayer;

            // Immediate pause on entry rather than waiting up to 2s for Room1UpkeepTick's next
            // tick - the periodic timer is what keeps it suppressed for as long as they stay on
            // this layer, this call just closes the gap right at the moment of the layer switch itself.
            if (targetLayer == RoomPlayerLayer) player.PauseFlyHackDetection();
        }

        #endregion

        #region Command surface

        [ChatCommand("rooms")]
        void CmdRooms(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player)) { player.ChatMessage("Ludo_Rooms: нет доступа (" + _config.AdminPermission + ")."); return; }

            Action<string> reply = msg => player.ChatMessage(msg);

            if (args.Length == 0)
            {
                reply("Использование: /rooms scan <радиус> [имя] | create <имя> | list | remove <номер> | join <номер> [команда] | leave | auto [команда] | layers | relayer <номер> | endgame <номер> | spawn add/list/remove <имя> ... | bed add/list/remove <имя> ... | lobby set <имя> | mainlobby set");
                return;
            }

            switch (args[0].ToLower())
            {
                case "scan":
                    if (args.Length < 2 || !float.TryParse(args[1], out var radius))
                    { reply($"Использование: /rooms scan <радиус> [имя] (по умолчанию радиус={_config.DefaultScanRadius})"); return; }
                    string templateName = args.Length >= 3 ? args[2] : "default";
                    DoScan(player, radius, templateName, reply);
                    break;

                case "create":
                    if (args.Length < 2) { reply("Использование: /rooms create <имя шаблона>"); return; }
                    DoCreate(args[1], reply);
                    break;

                case "relayer":
                    if (args.Length < 2 || !int.TryParse(args[1], out var relayerSlot))
                    { reply("Использование: /rooms relayer <номер комнаты>"); return; }
                    DoRelayer(relayerSlot, reply);
                    break;

                case "list": DoList(reply); break;

                case "remove":
                    if (args.Length < 2 || !int.TryParse(args[1], out var removeSlot))
                    { reply("Использование: /rooms remove <номер комнаты>"); return; }
                    DoRemove(removeSlot, reply);
                    break;

                case "join":
                    if (args.Length < 2 || !int.TryParse(args[1], out var joinSlot))
                    { reply("Использование: /rooms join <номер комнаты> [команда]"); return; }
                    DoJoin(player, joinSlot, args.Length >= 3 ? args[2] : null, reply);
                    break;

                case "leave": DoLeave(player, reply); break;

                case "auto": DoAuto(player, args.Length >= 2 ? args[1] : null, reply); break;

                case "layers": DoDumpLayers(reply); break;

                case "spawn": CmdSpawn(player, args, reply); break;

                case "bed": CmdBed(player, args, reply); break;

                case "lobby":
                    if (args.Length < 3 || args[1].ToLower() != "set") { reply("Использование: /rooms lobby set <шаблон>"); return; }
                    DoLobbySet(player, args[2], reply);
                    break;

                case "mainlobby":
                    if (args.Length < 2 || args[1].ToLower() != "set") { reply("Использование: /rooms mainlobby set"); return; }
                    DoMainLobbySet(player, reply);
                    break;

                case "endgame":
                    if (args.Length < 2 || !int.TryParse(args[1], out var endgameSlot))
                    { reply("Использование: /rooms endgame <номер комнаты>"); return; }
                    DoEndGame(endgameSlot, reply);
                    break;

                default: reply($"Ludo_Rooms: неизвестное действие '{args[0]}'."); break;
            }
        }

        // /rooms spawn add <шаблон> <команда>   - точка возрождения в текущей позиции игрока
        // /rooms spawn list <шаблон>
        // /rooms spawn remove <шаблон> <id>
        void CmdSpawn(BasePlayer player, string[] args, Action<string> reply)
        {
            if (args.Length < 2) { reply("Использование: /rooms spawn add/list/remove <шаблон> ..."); return; }

            switch (args[1].ToLower())
            {
                case "add":
                    if (args.Length < 4) { reply("Использование: /rooms spawn add <шаблон> <команда>"); return; }
                    DoSpawnAdd(player, args[2], args[3], reply);
                    break;

                case "list":
                    if (args.Length < 3) { reply("Использование: /rooms spawn list <шаблон>"); return; }
                    DoSpawnList(args[2], reply);
                    break;

                case "remove":
                    if (args.Length < 4 || !int.TryParse(args[3], out var spawnId))
                    { reply("Использование: /rooms spawn remove <шаблон> <id>"); return; }
                    DoSpawnRemove(args[2], spawnId, reply);
                    break;

                default: reply($"Ludo_Rooms: неизвестное действие spawn '{args[1]}'."); break;
            }
        }

        // /rooms bed add <шаблон> <команда>    - регистрирует ближайшую к игроку сущность шаблона
        //                                         как кровать команды (одна кровать на команду)
        // /rooms bed list <шаблон>
        // /rooms bed remove <шаблон> <id>
        void CmdBed(BasePlayer player, string[] args, Action<string> reply)
        {
            if (args.Length < 2) { reply("Использование: /rooms bed add/list/remove <шаблон> ..."); return; }

            switch (args[1].ToLower())
            {
                case "add":
                    if (args.Length < 4) { reply("Использование: /rooms bed add <шаблон> <команда>"); return; }
                    DoBedAdd(player, args[2], args[3], reply);
                    break;

                case "list":
                    if (args.Length < 3) { reply("Использование: /rooms bed list <шаблон>"); return; }
                    DoBedList(args[2], reply);
                    break;

                case "remove":
                    if (args.Length < 4 || !int.TryParse(args[3], out var bedId))
                    { reply("Использование: /rooms bed remove <шаблон> <id>"); return; }
                    DoBedRemove(args[2], bedId, reply);
                    break;

                default: reply($"Ludo_Rooms: неизвестное действие bed '{args[1]}'."); break;
            }
        }

        // Diagnostic: dumps all 32 Unity layer slots (0-31) by name - the hard technical ceiling
        // for layers is 32 (a layer mask is a 32-bit int), and Rust already occupies most of them
        // for its own systems. This is the only way to know for certain how many "ReservedN"
        // slots actually exist in this specific Rust build, rather than guessing.
        private void DoDumpLayers(Action<string> reply)
        {
            var lines = new List<string> { "Ludo_Rooms - все 32 слоя Unity в этой сборке Rust:" };
            int reservedCount = 0;
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) name = "(пусто/не используется)";
                if (name.StartsWith("Reserved")) reservedCount++;
                lines.Add($"{i}: {name}");
            }
            lines.Add($"Итого слоёв с именем 'ReservedN': {reservedCount}");

            string full = string.Join("\n", lines);
            Puts($"[Ludo_Rooms]\n{full}");
            reply($"Ludo_Rooms: список всех 32 слоёв записан в консоль сервера. Найдено 'ReservedN': {reservedCount}.");
        }

        [ConsoleCommand("rooms.scan")]
        void ConsoleScan(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (player == null) { arg.ReplyWith("rooms.scan требует вызова от игрока в игре (сканирует вокруг его позиции)."); return; }
            if (!arg.HasArgs(1) || !float.TryParse((string)arg.Args[0], out var radius))
            { arg.ReplyWith("Использование: rooms.scan <радиус> [имя]"); return; }
            string name = arg.HasArgs(2) ? (string)arg.Args[1] : "default";
            DoScan(player, radius, name, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.create")]
        void ConsoleCreate(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(1)) { arg.ReplyWith("Использование: rooms.create <имя шаблона>"); return; }
            DoCreate((string)arg.Args[0], arg.ReplyWith);
        }

        [ConsoleCommand("rooms.list")]
        void ConsoleList(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            DoList(arg.ReplyWith);
        }

        [ConsoleCommand("rooms.remove")]
        void ConsoleRemove(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(1) || !int.TryParse((string)arg.Args[0], out var slotId)) { arg.ReplyWith("Использование: rooms.remove <номер>"); return; }
            DoRemove(slotId, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.layers")]
        void ConsoleLayers(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            DoDumpLayers(arg.ReplyWith);
        }

        [ConsoleCommand("rooms.relayer")]
        void ConsoleRelayer(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(1) || !int.TryParse((string)arg.Args[0], out var slotId)) { arg.ReplyWith("Использование: rooms.relayer <номер>"); return; }
            DoRelayer(slotId, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.bedadd")]
        void ConsoleBedAdd(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (player == null) { arg.ReplyWith("rooms.bedadd требует вызова от игрока в игре (привязывает к его позиции)."); return; }
            if (!arg.HasArgs(2)) { arg.ReplyWith("Использование: rooms.bedadd <шаблон> <команда>"); return; }
            DoBedAdd(player, (string)arg.Args[0], (string)arg.Args[1], arg.ReplyWith);
        }

        [ConsoleCommand("rooms.bedlist")]
        void ConsoleBedList(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(1)) { arg.ReplyWith("Использование: rooms.bedlist <шаблон>"); return; }
            DoBedList((string)arg.Args[0], arg.ReplyWith);
        }

        [ConsoleCommand("rooms.bedremove")]
        void ConsoleBedRemove(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(2) || !int.TryParse((string)arg.Args[1], out var bedId)) { arg.ReplyWith("Использование: rooms.bedremove <шаблон> <id>"); return; }
            DoBedRemove((string)arg.Args[0], bedId, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.lobbyset")]
        void ConsoleLobbySet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (player == null) { arg.ReplyWith("rooms.lobbyset требует вызова от игрока в игре (привязывает к его позиции)."); return; }
            if (!arg.HasArgs(1)) { arg.ReplyWith("Использование: rooms.lobbyset <шаблон>"); return; }
            DoLobbySet(player, (string)arg.Args[0], arg.ReplyWith);
        }

        [ConsoleCommand("rooms.mainlobbyset")]
        void ConsoleMainLobbySet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (player == null) { arg.ReplyWith("rooms.mainlobbyset требует вызова от игрока в игре (привязывает к его позиции)."); return; }
            DoMainLobbySet(player, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.endgame")]
        void ConsoleEndGame(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !HasPermission(player)) return;
            if (!arg.HasArgs(1) || !int.TryParse((string)arg.Args[0], out var slotId)) { arg.ReplyWith("Использование: rooms.endgame <номер>"); return; }
            DoEndGame(slotId, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.join")]
        void ConsoleJoin(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !HasPermission(caller)) return;
            if (!arg.HasArgs(1) || !int.TryParse((string)arg.Args[0], out var slotId)) { arg.ReplyWith("Использование: rooms.join <номер> [steamid] [команда]"); return; }

            BasePlayer target = arg.HasArgs(2) ? BasePlayer.Find((string)arg.Args[1]) : caller;
            if (target == null) { arg.ReplyWith("Игрок не найден."); return; }
            string team = arg.HasArgs(3) ? (string)arg.Args[2] : null;
            DoJoin(target, slotId, team, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.leave")]
        void ConsoleLeave(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !HasPermission(caller)) return;

            BasePlayer target = arg.HasArgs(1) ? BasePlayer.Find((string)arg.Args[0]) : caller;
            if (target == null) { arg.ReplyWith("Игрок не найден."); return; }
            DoLeave(target, arg.ReplyWith);
        }

        [ConsoleCommand("rooms.auto")]
        void ConsoleAuto(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !HasPermission(caller)) return;

            BasePlayer target = arg.HasArgs(1) ? BasePlayer.Find((string)arg.Args[0]) : caller;
            if (target == null) { arg.ReplyWith("Игрок не найден."); return; }
            string team = arg.HasArgs(2) ? (string)arg.Args[1] : null;
            DoAuto(target, team, arg.ReplyWith);
        }

        #endregion

        #region Scan

        private class MarkersStoredData
        {
            public int NextId;
            public List<MarkerPointDto> Points = new List<MarkerPointDto>();
        }

        private class MarkerPointDto
        {
            public int Id;
            public float X, Y, Z;
        }

        private void DoScan(BasePlayer player, float radius, string templateName, Action<string> reply)
        {
            var center = player.transform.position;
            var buffer = new List<BaseEntity>();
            Vis.Entities(center, radius, buffer);

            var template = new Template
            {
                Name = templateName,
                ScanX = center.x, ScanY = center.y, ScanZ = center.z, ScanRadius = radius,
                ScannedAt = DateTime.UtcNow.ToString("o")
            };

            foreach (var ent in buffer)
            {
                if (ent == null || ent.IsDestroyed || ent is BasePlayer) continue;
                if (ent.OwnerID >= RoomOwnerIdBase && ent.OwnerID < RoomOwnerIdBase + 1000) continue; // don't re-scan our own duplicates

                string prefabLower = ent.PrefabName.ToLowerInvariant();
                if (_config.ExcludePrefabSubstrings.Any(sub => prefabLower.Contains(sub.ToLowerInvariant()))) continue;

                template.Entities.Add(TemplateEntity.From(ent));
            }

            float markerRadius = _config.MarkerCrossRefRadius > 0 ? _config.MarkerCrossRefRadius : radius;
            template.TeamMarkers = ScanMarkers(center, markerRadius);

            _data.Templates[templateName] = template;
            SaveData();

            // Fire-and-forget notification for Ludo_Markers (or anyone else) to refresh its own
            // cross-referenced copy of scan zones - CallHook is always safe to call even if nothing
            // is listening, so this doesn't create a hard dependency in either direction.
            Interface.Oxide.CallHook("OnLudoRoomsTemplateScanned", templateName);

            reply($"Ludo_Rooms: шаблон '{templateName}' сохранён - {template.Entities.Count} сущностей, {template.TeamMarkers.Count} маркеров команд (Ludo_Markers).");
        }

        // Reads Ludo_Markers' data/config directly rather than adding a hook to that plugin -
        // keeps the two plugins fully decoupled (Ludo_Markers never needs to know Ludo_Rooms
        // exists). Team identity is matched by the marker's configured display text, not by
        // position or color.
        private List<TemplateMarker> ScanMarkers(Vector3 center, float radius)
        {
            var result = new List<TemplateMarker>();
            try
            {
                var markerData = Interface.Oxide.DataFileSystem.ReadObject<MarkersStoredData>("Ludo_Markers");
                if (markerData?.Points == null) return result;

                var markerText = new Dictionary<string, string>();
                var markerColors = new Dictionary<string, string>();
                var configPath = System.IO.Path.Combine(Interface.Oxide.ConfigDirectory, "Ludo_Markers.json");
                if (System.IO.File.Exists(configPath))
                {
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(System.IO.File.ReadAllText(configPath));
                    if (parsed != null && parsed.TryGetValue("Displayed text per marker number (falls back to the number itself if not set here)", out var textObj))
                        markerText = JsonConvert.DeserializeObject<Dictionary<string, string>>(textObj.ToString()) ?? markerText;
                    if (parsed != null && parsed.TryGetValue("Color per marker number (\"r g b\" or \"r g b a\", 0-1 or 0-255)", out var colorObj))
                        markerColors = JsonConvert.DeserializeObject<Dictionary<string, string>>(colorObj.ToString()) ?? markerColors;
                }

                foreach (var point in markerData.Points)
                {
                    var pos = new Vector3(point.X, point.Y, point.Z);
                    if (Vector3.Distance(center, pos) > radius) continue;

                    string text = markerText.TryGetValue(point.Id.ToString(), out var t) ? t : point.Id.ToString();
                    string color = markerColors.TryGetValue(point.Id.ToString(), out var c) ? c : null;
                    result.Add(new TemplateMarker { MarkerId = point.Id, DisplayText = text, Color = color, X = point.X, Y = point.Y, Z = point.Z });
                }
            }
            catch (Exception ex)
            {
                Puts($"[Ludo_Rooms] Failed to cross-reference Ludo_Markers (non-fatal, team markers will be empty): {ex.Message}");
            }
            return result;
        }

        #endregion

        #region Room lifecycle

        private void DoCreate(string templateName, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден. Сначала /rooms scan."); return; }

            var slot = _data.Slots.FirstOrDefault(s => !s.InUse);
            if (slot == null) { reply("Ludo_Rooms: все комнаты заняты."); return; }

            slot.InUse = true;
            slot.TemplateName = templateName;
            SaveData();

            SetupCollisionIsolationForRoom(slot.SlotId);
            _roomEntities[slot.SlotId] = new List<BaseEntity>();
            SpawnBatch(template.Entities, 0, slot.SlotId, reply);
        }

        private void SpawnBatch(List<TemplateEntity> specs, int index, int slotId, Action<string> reply)
        {
            int processed = 0;
            while (index < specs.Count && processed < _config.BatchSize)
            {
                var spec = specs[index];
                var entity = GameManager.server.CreateEntity(spec.Prefab, spec.Pos, spec.Rot);
                if (entity != null)
                {
                    entity.OwnerID = RoomOwnerIdBase + (ulong)slotId;
                    _entityRoom[entity] = slotId; // tag BEFORE Spawn() - see plan's Phase 0 results
                    entity.Spawn();
                    ApplyLayerForRoom(entity, slotId);
                    ReapplyLayerNextTick(entity, slotId);
                    _roomEntities[slotId].Add(entity);
                }
                index++; processed++;
            }
            if (index < specs.Count)
                timer.Once(_config.BatchDelaySeconds, () => SpawnBatch(specs, index, slotId, reply));
            else
            {
                LinkBedsForRoom(slotId);
                reply?.Invoke($"Ludo_Rooms: комната #{slotId} создана, заспавнено {_roomEntities[slotId].Count} сущностей.");
            }
        }

        // Common to room creation, plugin reload, and match-end: re-derives which of a template's
        // registered beds are currently alive in this specific room by matching each
        // BedRegistration's saved position against whatever's actually still standing in
        // _roomEntities[slotId] (within BedMatchRadius). No live entity nearby = treated as
        // destroyed - this is deliberate (see plan's "bed restore vs no-teardown" note): a bed
        // killed during a match stays broken for the next match hosted in the same slot until an
        // admin rebuilds the room, rather than lying that it's alive again with no entity backing it.
        private void LinkBedsForRoom(int slotId)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot?.TemplateName == null || !_data.Templates.TryGetValue(slot.TemplateName, out var template)) return;
            if (!_matchState.TryGetValue(slotId, out var state)) _matchState[slotId] = state = new RoomMatchState();

            foreach (var kv in _bedEntityLookup.Where(kv => kv.Value.slotId == slotId).ToList())
                _bedEntityLookup.Remove(kv.Key);
            state.BedAlive.Clear();
            state.BedEntity.Clear();
            _roomEntities.TryGetValue(slotId, out var entities);

            foreach (var bed in template.Beds)
            {
                BaseEntity match = null;
                float best = float.MaxValue;
                foreach (var ent in entities ?? Enumerable.Empty<BaseEntity>())
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    float d = Vector3.Distance(ent.transform.position, bed.Position);
                    if (d < best && d <= _config.BedMatchRadius) { best = d; match = ent; }
                }
                state.BedAlive[bed.Team] = match != null;
                if (match != null) { state.BedEntity[bed.Team] = match; _bedEntityLookup[match] = (slotId, bed.Team); }
            }
        }

        private void DoList(Action<string> reply)
        {
            var lines = new List<string> { "Ludo_Rooms - комнаты:" };
            foreach (var slot in _data.Slots.OrderBy(s => s.SlotId))
            {
                if (!slot.InUse) { lines.Add($"- #{slot.SlotId}: свободна"); continue; }
                int entCount = _roomEntities.TryGetValue(slot.SlotId, out var list) ? list.Count : 0;
                lines.Add($"- #{slot.SlotId}: шаблон '{slot.TemplateName}', игроков: {slot.Players.Count}, сущностей: {entCount}");
            }
            reply(string.Join("\n", lines));
        }

        // One-off repair for rooms created before ReapplyLayerNextTick existed (or any room where
        // a BuildingBlock's collider drifted back onto the default layer some other way) - forces
        // every currently-tracked entity in the slot back onto StructureLayer without needing to
        // /rooms remove + /rooms create (which would drop the live match).
        private void DoRelayer(int slotId, Action<string> reply)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) { reply($"Ludo_Rooms: комната #{slotId} не активна."); return; }
            if (slotId != FullyIsolatedRoomSlot)
            { reply($"Ludo_Rooms: у комнаты #{slotId} нет коллизионной изоляции (её получает только комната #{FullyIsolatedRoomSlot})."); return; }
            if (!_config.EnableCollisionIsolation || !_collisionIsolationReady)
            { reply("Ludo_Rooms: коллизионная изоляция выключена или не готова."); return; }

            int count = 0;
            if (_roomEntities.TryGetValue(slotId, out var list))
                foreach (var ent in list)
                    if (ent != null && !ent.IsDestroyed) { ApplyLayerForRoom(ent, slotId); count++; }

            foreach (var kv in _liveBuiltRoomOf.Where(kv => kv.Value == slotId).ToList())
            {
                var ent = BaseNetworkable.serverEntities.Find(kv.Key) as BaseEntity;
                if (ent != null && !ent.IsDestroyed) { ApplyLayerForRoom(ent, slotId); count++; }
            }

            reply($"Ludo_Rooms: слой структур переприменён для {count} сущностей комнаты #{slotId}.");
        }

        private void DoRemove(int slotId, Action<string> reply)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) { reply($"Ludo_Rooms: комната #{slotId} не активна."); return; }

            // Mirrors EndMatch's "leave + teleport to main lobby" - a structural teardown via
            // /rooms remove strands players at the same coordinates as a room that's about to be
            // demolished otherwise, exactly like a match ending normally should.
            var departingPlayers = slot.Players.ToList(); // snapshot - DoLeaveInternal mutates slot.Players as it goes
            foreach (var userId in departingPlayers)
            {
                var p = BasePlayer.FindByID(userId);
                if (p == null) continue;
                DoLeaveInternal(p);
                if (_data.MainLobbyPoint != null) p.Teleport(_data.MainLobbyPoint.Position);
            }
            if (departingPlayers.Count > 0 && _data.MainLobbyPoint == null)
                Puts("[Ludo_Rooms] MainLobbyPoint not set (/rooms mainlobby set) - players were left in place.");

            var toKill = new List<BaseEntity>();
            if (_roomEntities.TryGetValue(slotId, out var list)) toKill.AddRange(list);

            foreach (var kv in _liveBuiltRoomOf.Where(kv => kv.Value == slotId).ToList())
            {
                var ent = BaseNetworkable.serverEntities.Find(kv.Key) as BaseEntity;
                if (ent != null) toKill.Add(ent);
                _liveBuiltRoomOf.Remove(kv.Key);
            }

            foreach (var ent in toKill) _entityRoom.Remove(ent);
            _roomEntities.Remove(slotId);

            if (_matchState.TryGetValue(slotId, out var matchState))
            {
                matchState.RoomTickTimer?.Destroy();
                matchState.MatchDurationTimer?.Destroy();
                _matchState.Remove(slotId);
            }
            foreach (var kv in _bedEntityLookup.Where(kv => kv.Value.slotId == slotId).ToList())
                _bedEntityLookup.Remove(kv.Key);

            slot.InUse = false;
            slot.TemplateName = null;
            slot.Players.Clear();
            SaveData();

            KillBatch(toKill, 0);
            reply($"Ludo_Rooms: комната #{slotId} снесена ({toKill.Count} сущностей).");
        }

        // team is optional. Without a team: unchanged legacy behavior, teleport to the "Центр"
        // Ludo_Markers cross-reference (spectator-ish, no match participation). With a team: the
        // player is registered into that room's match state and the teleport target depends on the
        // match phase - Live means reinforcing an ongoing match (straight to the team's spawn),
        // Lobby/Countdown means holding at the template's lobby point until GoLive sweeps everyone
        // in together (see the "Match state machine" region). The first team-holder to join while
        // the room is still in Lobby kicks off the countdown.
        private void DoJoin(BasePlayer player, int slotId, string team, Action<string> reply)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) { reply($"Ludo_Rooms: комната #{slotId} не активна."); return; }

            DoLeaveInternal(player); // leave any previous room first

            _playerRoom[player.userID] = slotId;
            if (!slot.Players.Contains(player.userID)) slot.Players.Add(player.userID);
            SaveData();

            var template = GetTemplate(slot);
            if (!_matchState.TryGetValue(slotId, out var state)) _matchState[slotId] = state = new RoomMatchState();

            Vector3? teleportPos;
            if (team != null)
            {
                state.Players[player.userID] = new PlayerMatchState { Team = team };
                EnsureAvatar(player.userID);

                teleportPos = state.Phase == MatchPhase.Live
                    ? ResolveTeamSpawn(template, team) ?? ResolveCenterMarker(template)
                    : template?.LobbyPoint?.Position ?? ResolveTeamSpawn(template, team) ?? ResolveCenterMarker(template);

                if (state.Phase == MatchPhase.Lobby) TryStartCountdown(slotId);
            }
            else
            {
                teleportPos = ResolveCenterMarker(template);
            }
            if (teleportPos != null) player.Teleport(teleportPos.Value);

            ApplyPlayerLayerForRoom(player, slotId);
            RefreshVisibilityFor(player);
            reply($"Ludo_Rooms: вы в комнате #{slotId}" + (team != null ? $", команда '{team}'" : "") + ".");
        }

        private Template GetTemplate(RoomSlot slot) =>
            slot?.TemplateName != null && _data.Templates.TryGetValue(slot.TemplateName, out var t) ? t : null;

        private Vector3? ResolveTeamSpawn(Template template, string team)
        {
            var sp = template?.SpawnPoints.FirstOrDefault(x => string.Equals(x.Team, team, StringComparison.OrdinalIgnoreCase));
            return sp?.Position;
        }

        private Vector3? ResolveCenterMarker(Template template)
        {
            var centerMarker = template?.TeamMarkers.FirstOrDefault(m => m.DisplayText.IndexOf("Центр", StringComparison.OrdinalIgnoreCase) >= 0);
            return centerMarker?.Position;
        }

        private void DoLeaveInternal(BasePlayer player)
        {
            if (!_playerRoom.TryGetValue(player.userID, out var oldSlot)) return;
            _playerRoom.Remove(player.userID);
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == oldSlot);
            slot?.Players.Remove(player.userID);
            SaveData();
            ApplyPlayerLayerForRoom(player, null);
            RefreshVisibilityFor(player);

            if (_matchState.TryGetValue(oldSlot, out var state)) state.Players.Remove(player.userID);
            DestroyMatchUi(player);
            _avatarRequested.Remove(player.userID);
        }

        private void DoLeave(BasePlayer player, Action<string> reply)
        {
            if (!_playerRoom.ContainsKey(player.userID)) { reply("Ludo_Rooms: вы не в комнате."); return; }
            DoLeaveInternal(player);
            reply("Ludo_Rooms: вы покинули комнату.");
        }

        private void DoAuto(BasePlayer player, string team, Action<string> reply)
        {
            var slot = _data.Slots.Where(s => s.InUse).OrderBy(s => s.Players.Count).FirstOrDefault();
            if (slot == null) { reply("Ludo_Rooms: нет активных комнат. Сначала /rooms create."); return; }
            DoJoin(player, slot.SlotId, team, reply);
        }

        // /rooms spawn add <шаблон> <команда> - respawn-точка в текущей позиции игрока, той же
        // логикой что и Ludo_Markers: встал, вызвал команду, точка сохранена под номером.
        private void DoSpawnAdd(BasePlayer player, string templateName, string team, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден. Сначала /rooms scan."); return; }

            var pos = player.transform.position;
            var point = new SpawnPoint { Id = template.NextSpawnPointId++, Team = team, X = pos.x, Y = pos.y, Z = pos.z };
            template.SpawnPoints.Add(point);
            _data.Templates[templateName] = template;
            SaveData();

            reply($"Ludo_Rooms: точка возрождения #{point.Id} для команды '{team}' добавлена в шаблон '{templateName}'.");
        }

        private void DoSpawnList(string templateName, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден."); return; }

            if (template.SpawnPoints.Count == 0) { reply($"Ludo_Rooms: в шаблоне '{templateName}' пока нет точек возрождения."); return; }

            var lines = new List<string> { $"Ludo_Rooms - точки возрождения шаблона '{templateName}':" };
            foreach (var sp in template.SpawnPoints)
                lines.Add($"- #{sp.Id}: команда '{sp.Team}' в ({sp.X:F1}, {sp.Y:F1}, {sp.Z:F1})");
            reply(string.Join("\n", lines));
        }

        private void DoSpawnRemove(string templateName, int spawnId, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден."); return; }

            int removed = template.SpawnPoints.RemoveAll(sp => sp.Id == spawnId);
            SaveData();
            reply(removed > 0
                ? $"Ludo_Rooms: точка возрождения #{spawnId} удалена из '{templateName}'."
                : $"Ludo_Rooms: точка возрождения #{spawnId} не найдена в '{templateName}'.");
        }

        // Matches the nearest TemplateEntity (not a live world entity - the template itself) to the
        // admin's current position, within BedMatchRadius - same "stand at the spot" idiom as
        // DoSpawnAdd, but resolved against the scanned template data since that's what LinkBedsForRoom
        // later re-matches against per-room. One bed per team per template: a second /rooms bed add
        // for the same team replaces the earlier registration rather than adding a duplicate.
        private void DoBedAdd(BasePlayer player, string templateName, string team, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден. Сначала /rooms scan."); return; }

            var pos = player.transform.position;
            TemplateEntity nearest = null;
            float best = float.MaxValue;
            foreach (var ent in template.Entities)
            {
                float d = Vector3.Distance(pos, ent.Pos);
                if (d < best) { best = d; nearest = ent; }
            }
            if (nearest == null || best > _config.BedMatchRadius)
            {
                reply($"Ludo_Rooms: рядом (< {_config.BedMatchRadius}м) нет ни одной сущности шаблона '{templateName}' - встаньте вплотную к кровати.");
                return;
            }

            template.Beds.RemoveAll(b => string.Equals(b.Team, team, StringComparison.OrdinalIgnoreCase));
            var bed = new BedRegistration { Id = template.NextBedId++, Team = team, X = nearest.Pos.x, Y = nearest.Pos.y, Z = nearest.Pos.z };
            template.Beds.Add(bed);
            SaveData();

            // Re-link every already-built room of this template immediately, so an admin fixing up
            // a bed registration doesn't need to /rooms remove + /rooms create to see it take effect.
            foreach (var slot in _data.Slots.Where(s => s.InUse && s.TemplateName == templateName))
                LinkBedsForRoom(slot.SlotId);

            reply($"Ludo_Rooms: кровать #{bed.Id} команды '{team}' зарегистрирована в шаблоне '{templateName}' (сущность в {best:F1}м).");
        }

        private void DoBedList(string templateName, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден."); return; }

            if (template.Beds.Count == 0) { reply($"Ludo_Rooms: в шаблоне '{templateName}' пока нет зарегистрированных кроватей."); return; }

            var lines = new List<string> { $"Ludo_Rooms - кровати шаблона '{templateName}':" };
            foreach (var bed in template.Beds)
                lines.Add($"- #{bed.Id}: команда '{bed.Team}' в ({bed.X:F1}, {bed.Y:F1}, {bed.Z:F1})");
            reply(string.Join("\n", lines));
        }

        private void DoBedRemove(string templateName, int bedId, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден."); return; }

            int removed = template.Beds.RemoveAll(b => b.Id == bedId);
            SaveData();
            foreach (var slot in _data.Slots.Where(s => s.InUse && s.TemplateName == templateName))
                LinkBedsForRoom(slot.SlotId);

            reply(removed > 0
                ? $"Ludo_Rooms: кровать #{bedId} удалена из '{templateName}'."
                : $"Ludo_Rooms: кровать #{bedId} не найдена в '{templateName}'.");
        }

        // /rooms lobby set <шаблон> - точка предматчевого ожидания и точка, куда телепортирует на
        // респавн-кулдаун после смерти (см. OnPlayerRespawned) - одна точка на шаблон, перезаписывается.
        private void DoLobbySet(BasePlayer player, string templateName, Action<string> reply)
        {
            if (!_data.Templates.TryGetValue(templateName, out var template))
            { reply($"Ludo_Rooms: шаблон '{templateName}' не найден. Сначала /rooms scan."); return; }

            var pos = player.transform.position;
            template.LobbyPoint = new LobbyPointDto { X = pos.x, Y = pos.y, Z = pos.z };
            SaveData();
            reply($"Ludo_Rooms: точка лобби шаблона '{templateName}' установлена в текущей позиции.");
        }

        // /rooms mainlobby set - единая точка на весь сервер (не per-template), куда игроков
        // телепортирует по завершению матча (см. EndMatch).
        private void DoMainLobbySet(BasePlayer player, Action<string> reply)
        {
            var pos = player.transform.position;
            _data.MainLobbyPoint = new LobbyPointDto { X = pos.x, Y = pos.y, Z = pos.z };
            SaveData();
            reply("Ludo_Rooms: главное лобби установлено в текущей позиции.");
        }

        private void DoEndGame(int slotId, Action<string> reply)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) { reply($"Ludo_Rooms: комната #{slotId} не активна."); return; }
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase == MatchPhase.Lobby)
            { reply($"Ludo_Rooms: в комнате #{slotId} сейчас нет активного матча."); return; }

            EndMatch(slotId, "остановлен администратором");
            reply($"Ludo_Rooms: матч в комнате #{slotId} остановлен.");
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null || !_playerRoom.TryGetValue(player.userID, out var slotId)) return;
            _playerRoom.Remove(player.userID);
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            slot?.Players.Remove(player.userID);
            SaveData();

            if (_matchState.TryGetValue(slotId, out var state)) state.Players.Remove(player.userID);
            DestroyMatchUi(player);
            _avatarRequested.Remove(player.userID);
        }

        #endregion

        #region Match state machine (Lobby -> Countdown -> Live -> reset to Lobby)

        private void BroadcastToRoom(int slotId, string message)
        {
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null) return;
            foreach (var userId in slot.Players)
            {
                var p = BasePlayer.FindByID(userId);
                p?.ChatMessage(message);
            }
        }

        // Idempotent - only actually starts things the first time a room has >=1 team-holder while
        // still in Lobby. Called from DoJoin every time a team is (re-)assigned, so it must be safe
        // to call repeatedly without resetting an already-running countdown.
        private void TryStartCountdown(int slotId)
        {
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase != MatchPhase.Lobby) return;
            if (state.Players.Count == 0) return;

            state.Phase = MatchPhase.Countdown;
            state.SecondsRemaining = _config.LobbyCountdownSeconds;
            state.RoomTickTimer = timer.Every(_config.MatchTickSeconds, () => RoomTick(slotId));
            BroadcastToRoom(slotId, $"Ludo_Rooms: обратный отсчёт начался ({_config.LobbyCountdownSeconds}с)!");
        }

        // Single 1Hz driver for both Countdown and Live, rather than juggling two timer lifecycles.
        // Players who join with a team mid-countdown are swept in automatically at GoLive - DoJoin
        // just writes into the same state.Players dictionary this reads, no separate "late joiner"
        // path needed.
        private void RoomTick(int slotId)
        {
            if (!_matchState.TryGetValue(slotId, out var state)) return;
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) { state.RoomTickTimer?.Destroy(); return; }

            if (state.Phase == MatchPhase.Countdown)
            {
                if (--state.SecondsRemaining <= 0) { GoLive(slotId); return; }
            }
            else if (state.Phase == MatchPhase.Live)
            {
                foreach (var kv in state.Players.ToList())
                {
                    var pms = kv.Value;
                    if (pms.Alive || pms.Eliminated) continue;
                    if (--pms.CooldownRemaining > 0) continue;
                    ResolveCooldownElapsed(slotId, slot, state, kv.Key, pms);
                }
                CheckWinCondition(slotId);
            }
            RefreshCui(slotId, state, slot);
        }

        private void GoLive(int slotId)
        {
            if (!_matchState.TryGetValue(slotId, out var state)) return;
            if (state.Players.Count == 0) { state.Phase = MatchPhase.Lobby; state.RoomTickTimer?.Destroy(); return; } // nobody left mid-countdown

            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            var template = GetTemplate(slot);
            state.Phase = MatchPhase.Live;

            foreach (var kv in state.Players)
            {
                kv.Value.Alive = true;
                kv.Value.Eliminated = false;
                var bp = BasePlayer.FindByID(kv.Key);
                var spawnPos = ResolveTeamSpawn(template, kv.Value.Team) ?? ResolveCenterMarker(template);
                if (bp != null && spawnPos != null) bp.Teleport(spawnPos.Value);
            }

            state.MatchDurationTimer = timer.Once(_config.MatchDurationSeconds, () => EndMatch(slotId, "истекло время матча"));
            BroadcastToRoom(slotId, "Ludo_Rooms: матч начался!");
        }

        // Fires from RoomTick once a dead player's respawn cooldown hits 0. Bed still alive -> back
        // in the fight at the team's spawn. Bed destroyed -> permanently eliminated for the rest of
        // this match, left parked at the lobby point (no further auto-respawn attempts).
        private void ResolveCooldownElapsed(int slotId, RoomSlot slot, RoomMatchState state, ulong userId, PlayerMatchState pms)
        {
            bool bedAlive = state.BedAlive.TryGetValue(pms.Team, out var alive) && alive;
            var bp = BasePlayer.FindByID(userId);

            if (!bedAlive)
            {
                pms.Eliminated = true;
                if (bp != null) bp.ChatMessage("Ludo_Rooms: ваша кровать разрушена - в этом матче вы выбыли.");
                return;
            }

            // pms.Alive is set BEFORE Respawn() specifically so OnPlayerRespawned's own guard
            // (!pms.Alive) sees it as already true and skips its lobby-point teleport - Respawn()
            // fires that hook as part of actually reviving the player, and we want THIS spawn
            // point (the team's), not the "still on cooldown" lobby one. Respawn() itself is
            // required here, not just Teleport() - the player is still in Rust's own dead/
            // waiting-to-respawn state at this point (confirmed BasePlayer.Respawn() exists via a
            // byte-scan of the installed Assembly-CSharp.dll), and moving a dead player's transform
            // alone does not revive them - that's exactly why auto-respawn wasn't working before.
            pms.Alive = true;
            var template = GetTemplate(slot);
            var spawnPos = ResolveTeamSpawn(template, pms.Team) ?? ResolveCenterMarker(template);
            if (bp != null)
            {
                bp.Respawn();
                if (spawnPos != null) bp.Teleport(spawnPos.Value);
            }
        }

        // A team is "eliminated" once its bed is destroyed AND it currently has zero alive players -
        // a destroyed bed alone doesn't eliminate a team still fighting with players up right now.
        // Needs >=2 distinct teams among currently-present state.Players to ever fire naturally (see
        // plan's risk #5) - a single-team room only ends via match-duration timer or /rooms endgame.
        private void CheckWinCondition(int slotId)
        {
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase != MatchPhase.Live) return;

            var teams = state.Players.Values.Select(p => p.Team).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (teams.Count < 2) return;

            var contesting = teams.Where(team =>
            {
                bool bedAlive = state.BedAlive.TryGetValue(team, out var alive) && alive;
                bool anyAlivePlayer = state.Players.Values.Any(p => string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase) && p.Alive);
                return bedAlive || anyAlivePlayer;
            }).ToList();

            if (contesting.Count <= 1)
                EndMatch(slotId, contesting.Count == 1 ? $"команда '{contesting[0]}' победила" : "все команды выбыли");
        }

        // Converges all three end triggers (duration timer, win condition, /rooms endgame).
        // Idempotent via the Phase==Lobby guard, since a duration-timer fire and a win-condition
        // detection can plausibly land on the same tick.
        private void EndMatch(int slotId, string reasonRu)
        {
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase == MatchPhase.Lobby) return;
            state.RoomTickTimer?.Destroy();
            state.MatchDurationTimer?.Destroy();

            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            foreach (var userId in (slot?.Players ?? new List<ulong>()).ToList())
            {
                var p = BasePlayer.FindByID(userId);
                if (p == null) continue;
                p.ChatMessage($"Ludo_Rooms: матч в комнате #{slotId} завершён ({reasonRu}).");
                // Must fully leave the room, not just teleport - the main lobby is a single point
                // shared by every room, so keeping room membership would make CanNetworkTo hide
                // players of one just-finished match from another's, standing at the same spot.
                DoLeaveInternal(p);
                if (_data.MainLobbyPoint != null) p.Teleport(_data.MainLobbyPoint.Position);
            }
            if (_data.MainLobbyPoint == null)
                Puts("[Ludo_Rooms] MainLobbyPoint not set (/rooms mainlobby set) - players were left in place.");

            state.Players.Clear();
            LinkBedsForRoom(slotId); // re-derive bed-alive from actual entity presence, not force-true
            state.Phase = MatchPhase.Lobby;
        }

        #endregion

        #region Live-build isolation

        // Fires after a player places a deployable/building block via the normal Planner flow -
        // unlike our own template duplicates, this entity is already spawned/broadcast through
        // the standard distance-based path by the time this hook runs, so "tag before Spawn()"
        // isn't available here; anyone already nearby and wrong-room needs an explicit revoke.
        // OwnerID is left untouched (drives Tool Cupboard auth/decay/sharing for the real
        // builder) - room membership lives in the separate _liveBuiltRoomOf side-table instead.
        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var player = plan?.GetOwnerPlayer();
            var ent = go?.ToBaseEntity();
            if (player == null || ent == null || ent.net == null) return;
            if (!_playerRoom.TryGetValue(player.userID, out var slotId)) return;

            _liveBuiltRoomOf[ent.net.ID] = slotId;
            ApplyLayerForRoom(ent, slotId);

            foreach (var other in BasePlayer.activePlayerList)
            {
                if (other == null || other.userID == player.userID) continue;
                int? otherRoom = _playerRoom.TryGetValue(other.userID, out var or) ? or : (int?)null;
                if (otherRoom != slotId) HideFrom(ent, other);
            }
        }

        #endregion

        #region Damage isolation safety net

        // Defense-in-depth for any AoE/splash code path that queries physics directly rather than
        // respecting per-connection networking/collision - CanNetworkTo + layer isolation should
        // already prevent cross-room hits from happening at all.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return null;
            int? victimRoom = GetEntityRoom(entity);
            if (victimRoom == null) return null;

            int? attackerRoom = null;
            if (info.InitiatorPlayer != null)
                attackerRoom = _playerRoom.TryGetValue(info.InitiatorPlayer.userID, out var ar) ? ar : (int?)null;
            else if (info.Initiator != null)
                attackerRoom = GetEntityRoom(info.Initiator);

            if (attackerRoom == null || attackerRoom == victimRoom) return null;

            info.damageTypes?.Clear();
            return true;
        }

        #endregion

        #region Anti-hack exemption inside arenas

        // The realization that makes per-room collision work WITHOUT layers, map copies or
        // sub-servers: Rust movement is client-authoritative. The client simulates its own physics
        // against only the entities it has been SENT - and CanNetworkTo already withholds foreign
        // rooms' entities, so every client already walks straight through them. What produced the
        // "invisible phantom wall" jitter/knockback/flyhack kicks was never collision at all - it
        // was the SERVER's anti-hack validation, which still has those entities in its own world
        // and flags the player as noclipping through them (or as flying, when standing on an
        // own-room structure whose collider we'd moved off the server's ground mask). Server-side
        // layer edits were never even visible to clients (GameObject.layer isn't networked), which
        // is also why the occupied-layer/excludeLayers spikes "did nothing": the tester's client
        // kept colliding with the visible test wall's prefab-default layers regardless of anything
        // set server-side.
        //
        // So the actual fix is to cancel exactly those violations - only for room members, only
        // within their arena's scanned footprint, so anti-cheat everywhere else on the map stays
        // fully intact. Hook confirmed present in this exact build via byte-scan of
        // Assembly-CSharp.dll ("OnAntihackViolation", alongside per-type variants). Known
        // trade-off: a room member inside the arena radius gets noclip/flyhack leniency there, so
        // an actual cheater could abuse it WITHIN the arena bounds - acceptable for a minigame
        // zone, and the rest of the map is unaffected.
        private object OnAntihackViolation(BasePlayer player, AntiHackType type, float amount)
        {
            if (player == null || !_playerRoom.TryGetValue(player.userID, out var slotId)) return null;
            if (type != AntiHackType.NoClip && type != AntiHackType.FlyHack) return null;

            var template = GetTemplate(_data.Slots.FirstOrDefault(s => s.SlotId == slotId));
            if (template == null || template.ScanRadius <= 0f) return null;

            var center = new Vector3(template.ScanX, template.ScanY, template.ScanZ);
            if (Vector3.Distance(player.transform.position, center) > template.ScanRadius + 30f) return null;

            return false; // any non-null return cancels the violation (no rubber-band, no kick)
        }

        #endregion

        #region Combat: death/respawn cooldown, bed destruction

        // Bookkeeping only - deliberately no teleport here. At the instant this fires the player is
        // mid-ragdoll/death-transition, not a reliably teleportable entity yet (see OnPlayerRespawned
        // for where the position fix actually happens). The visible cooldown counts down from the
        // real moment of death regardless of when the player actually clicks respawn.
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || !_playerRoom.TryGetValue(player.userID, out var slotId)) return;
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase != MatchPhase.Live) return;
            if (!state.Players.TryGetValue(player.userID, out var pms) || !pms.Alive) return;

            pms.Alive = false;
            pms.CooldownRemaining = _config.RespawnCooldownSeconds;
        }

        // Rust puts a respawning player wherever its own spawn-selection logic decides (bag, random,
        // whatever) - if they're still serving out a cooldown, immediately correct that to the
        // room's lobby point. Reuses player.Teleport(...), the same call already proven throughout
        // this file (DoJoin/GoLive/ResolveCooldownElapsed), rather than RespawnAt/IsSleeping-based
        // approaches - grepped the whole plugins folder and found no confirmed use of RespawnAt
        // anywhere in this codebase, so not relying on it.
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !_playerRoom.TryGetValue(player.userID, out var slotId)) return;
            if (!_matchState.TryGetValue(slotId, out var state) || state.Phase != MatchPhase.Live) return;
            if (!state.Players.TryGetValue(player.userID, out var pms) || pms.Alive) return; // only park mid-cooldown

            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            var template = GetTemplate(slot);
            if (template?.LobbyPoint != null) player.Teleport(template.LobbyPoint.Position);
        }

        // Fires on ANY entity destruction (combat death, decay, demolish, our own DoRemove's
        // KillBatch) - chosen over OnEntityDeath because a bed prefab isn't guaranteed to be a
        // BaseCombatEntity, and OnEntityKill is the one hook that's universal regardless. The
        // Phase == Live guard is what stops this from mis-firing when DoRemove tears its own room
        // down (that KillBatch call kills the same entities this is watching).
        private void OnEntityKill(BaseNetworkable entity)
        {
            var be = entity as BaseEntity;
            if (be == null || !_bedEntityLookup.TryGetValue(be, out var info)) return;
            _bedEntityLookup.Remove(be);

            if (!_matchState.TryGetValue(info.slotId, out var state) || state.Phase != MatchPhase.Live) return;
            state.BedAlive[info.team] = false;
            state.BedEntity.Remove(info.team);
            BroadcastToRoom(info.slotId, $"Ludo_Rooms: кровать команды '{info.team}' уничтожена!");
            CheckWinCondition(info.slotId);
        }

        #endregion

        #region CUI: scoreboard + countdown

        private const string CountdownLayer = "LudoRooms.Countdown";
        private const string ScoreboardLayer = "LudoRooms.Scoreboard";

        private static readonly Regex AvatarRegex = new Regex(@"<avatarFull><!\[CDATA\[(.*)\]\]></avatarFull>");

        // Fired once per player per session, the moment they're first registered into a room's
        // match state (DoJoin) - not on every refresh tick. Under normal operation GetImage()
        // always returns a usable string (cached PNG, or ImageLibrary's own LOADING/NONE
        // placeholder while not cached yet) - the PrintWarning calls below are for the failure
        // paths (Steam fetch failed, or the avatar tag couldn't be parsed from the response),
        // which previously failed completely silently, making a stuck/missing avatar unfixable to
        // diagnose from the server console.
        private void EnsureAvatar(ulong userId)
        {
            if (ImageLibrary == null || !_avatarRequested.Add(userId)) return;
            webrequest.Enqueue($"http://steamcommunity.com/profiles/{userId}?xml=1", null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintWarning($"[Ludo_Rooms] Не удалось загрузить аватар для {userId} (HTTP {code}).");
                    return;
                }
                string avatar = AvatarRegex.Match(response).Groups[1].ToString();
                if (string.IsNullOrEmpty(avatar))
                {
                    PrintWarning($"[Ludo_Rooms] Не удалось разобрать аватар для {userId} из ответа Steam.");
                    return;
                }
                // imageId is a required parameter on AddImage (no default), unlike GetImage's
                // optional trailing args - omitting it silently no-ops the call.
                ImageLibrary.Call("AddImage", avatar, $"avatar_{userId}", 0UL);
            }, this);
        }

        // Resolves a free-text team string to a CUI color by name-matching it against this
        // template's Ludo_Markers-sourced TeamMarkers (same case-insensitive convention used
        // everywhere else team strings are matched in this file). Admins need marker text and team
        // text to agree for coloring to work - unmatched degrades to neutral gray, not a crash.
        private string GetTeamColorCui(Template template, string team)
        {
            var marker = template?.TeamMarkers.FirstOrDefault(m => string.Equals(m.DisplayText, team, StringComparison.OrdinalIgnoreCase));
            return marker?.Color != null ? ParseTeamColorCui(marker.Color) : "0.6 0.6 0.6 1";
        }

        // Ludo_Markers stores "r g b" or "r g b a", either 0-1 or 0-255 - port of its own color
        // parsing convention, output as a CUI-ready "r g b a" 0-1 string.
        private string ParseTeamColorCui(string rgba)
        {
            var parts = (rgba ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return "1 1 1 1";
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                return "1 1 1 1";
            float a = 1f;
            if (parts.Length > 3) float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            if (r > 1f || g > 1f || b > 1f) { r /= 255f; g /= 255f; b /= 255f; if (a > 1f) a /= 255f; }
            return $"{Mathf.Clamp01(r):F3} {Mathf.Clamp01(g):F3} {Mathf.Clamp01(b):F3} {Mathf.Clamp01(a):F3}";
        }

        // Built once per room per tick (content is identical for every viewer - nobody's avatar/
        // name/status differs by who's looking) and pushed to every connected member of the room,
        // rather than rebuilding per-viewer.
        private void RefreshCui(int slotId, RoomMatchState state, RoomSlot slot)
        {
            var viewers = slot.Players.Select(id => BasePlayer.FindByID(id)).Where(p => p != null && p.IsConnected).ToList();
            if (viewers.Count == 0) return;

            var container = new CuiElementContainer();

            if (state.Phase == MatchPhase.Countdown)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.5" },
                    RectTransform = { AnchorMin = "0.4 0.93", AnchorMax = "0.6 0.98" }
                }, "Overlay", CountdownLayer);
                container.Add(new CuiLabel
                {
                    Text = { Text = $"Матч начнётся через: {state.SecondsRemaining}с", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, CountdownLayer);
            }

            var template = GetTemplate(slot);
            var teams = state.Players.Values.Select(p => p.Team).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Panel height is computed from actual content (teams + rows) rather than a fixed
            // box - with a fixed box, a 1-2 player room left most of it as dead, empty overlay
            // (reported as "stretched and uninformative"). Anchored to hang down from a fixed top
            // edge (just under the countdown panel) so it grows downward as the roster grows.
            const float rowH = 0.045f, headerH = 0.035f, panelTop = 0.92f, panelLeft = 0.25f, panelRight = 0.75f;
            float contentHeight = teams.Count * headerH + state.Players.Count * rowH;
            float panelBottom = Mathf.Max(0.15f, panelTop - contentHeight);

            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = $"{panelLeft} {panelBottom}", AnchorMax = $"{panelRight} {panelTop}" }
            }, "Overlay", ScoreboardLayer);

            // y is a LOCAL fraction (0-1) of the panel just created above, which is now sized to
            // exactly fit teams.Count headers + state.Players.Count rows - so this fills it
            // edge-to-edge with no leftover space, instead of a fixed box with content only at top.
            float y = 1f;
            foreach (var team in teams)
            {
                y -= headerH;
                string headerName = $"{ScoreboardLayer}.Header.{team}";
                container.Add(new CuiPanel
                {
                    Image = { Color = GetTeamColorCui(template, team) },
                    RectTransform = { AnchorMin = $"0 {y}", AnchorMax = $"1 {y + headerH}" }
                }, ScoreboardLayer, headerName);
                container.Add(new CuiLabel
                {
                    Text = { Text = team, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.02 0", AnchorMax = "1 1" }
                }, headerName);

                foreach (var kv in state.Players.Where(p => string.Equals(p.Value.Team, team, StringComparison.OrdinalIgnoreCase)))
                {
                    y -= rowH;
                    string rowName = $"{ScoreboardLayer}.Row.{kv.Key}";
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "1 1 1 0.05" },
                        RectTransform = { AnchorMin = $"0 {y}", AnchorMax = $"1 {y + rowH}" }
                    }, ScoreboardLayer, rowName);

                    // Only render the RawImage if GetImage actually returned something - a null/
                    // empty Png (e.g. ImageLibrary.Call failing, or its own placeholder images not
                    // having finished loading yet) renders as garbage/static in Unity rather than
                    // failing cleanly, so skipping the element entirely is the safe fallback.
                    string avatarPng = ImageLibrary != null ? ImageLibrary.Call("GetImage", $"avatar_{kv.Key}") as string : null;
                    if (!string.IsNullOrEmpty(avatarPng))
                    {
                        container.Add(new CuiElement
                        {
                            Parent = rowName,
                            Components =
                            {
                                new CuiRawImageComponent { Png = avatarPng },
                                new CuiRectTransformComponent { AnchorMin = "0.01 0.1", AnchorMax = "0.12 0.9" }
                            }
                        });
                    }

                    var bp = BasePlayer.FindByID(kv.Key);
                    string name = bp != null ? bp.displayName : kv.Key.ToString();
                    string status = kv.Value.Eliminated ? "выбыл" : kv.Value.Alive ? "жив" : $"КД: {kv.Value.CooldownRemaining}с";
                    string statusColor = kv.Value.Eliminated ? "0.6 0.2 0.2 1" : kv.Value.Alive ? "0.3 0.9 0.3 1" : "0.9 0.8 0.2 1";

                    container.Add(new CuiLabel
                    {
                        Text = { Text = name, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = "0.15 0", AnchorMax = "0.65 1" }
                    }, rowName);
                    container.Add(new CuiLabel
                    {
                        Text = { Text = status, FontSize = 12, Align = TextAnchor.MiddleRight, Color = statusColor },
                        RectTransform = { AnchorMin = "0.65 0", AnchorMax = "0.98 1" }
                    }, rowName);
                }
            }

            foreach (var viewer in viewers)
            {
                CuiHelper.DestroyUi(viewer, ScoreboardLayer);
                CuiHelper.DestroyUi(viewer, CountdownLayer);
                CuiHelper.AddUi(viewer, container);
            }
        }

        private void DestroyMatchUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, ScoreboardLayer);
            CuiHelper.DestroyUi(player, CountdownLayer);
        }

        #endregion

        #region External API

        [HookMethod("Rooms_AssignPlayer")]
        public bool Rooms_AssignPlayer(BasePlayer player, int slotId, string team = null)
        {
            if (player == null) return false;
            var slot = _data.Slots.FirstOrDefault(s => s.SlotId == slotId);
            if (slot == null || !slot.InUse) return false;
            DoJoin(player, slotId, team, _ => { });
            return true;
        }

        [HookMethod("Rooms_UnassignPlayer")]
        public void Rooms_UnassignPlayer(BasePlayer player)
        {
            if (player != null) DoLeaveInternal(player);
        }

        [HookMethod("Rooms_GetPlayerRoom")]
        public int Rooms_GetPlayerRoom(BasePlayer player) =>
            player != null && _playerRoom.TryGetValue(player.userID, out var r) ? r : -1;

        [HookMethod("Rooms_CreateFromTemplate")]
        public int Rooms_CreateFromTemplate(string templateName)
        {
            var slot = _data.Slots.FirstOrDefault(s => !s.InUse);
            if (slot == null || !_data.Templates.ContainsKey(templateName)) return -1;
            DoCreate(templateName, _ => { });
            return slot.SlotId;
        }

        [HookMethod("Rooms_Teardown")]
        public void Rooms_Teardown(int slotId) => DoRemove(slotId, _ => { });

        [HookMethod("Rooms_GetFreeSlot")]
        public int Rooms_GetFreeSlot() => _data.Slots.FirstOrDefault(s => !s.InUse)?.SlotId ?? -1;

        #endregion
    }
}
