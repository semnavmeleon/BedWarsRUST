using System;
using System.Collections.Generic;
using System.Linq;
using Network;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Ludo_RoomsSpike", "Semnavmeleon", "0.1.0")]
    [Description("Throwaway Phase 0 spike for Ludo_Rooms - proves (or disproves) that Physics.IgnoreCollision lets a player walk through a co-located, differently-roomed structure while CanNetworkTo hides it from them, before the real scan/duplicate/room pipeline gets built on top of that assumption. Delete once validated.")]
    public class Ludo_RoomsSpike : RustPlugin
    {
        #region Fields

        private const string AdminPermission = "ludoroomsspike.admin";
        private const ulong SpikeOwnerId = 999999999900000001UL; // distinct range from Ludo_Musika's MarkerOwnerId and the eventual Ludo_Rooms range

        // Keyed by entity reference, not net.ID - net.ID/`entity.net` isn't allocated until
        // Spawn() runs, so tagging before Spawn() (required - see SpawnWall/SpawnBox) can only
        // key off the object reference itself, which exists immediately after CreateEntity().
        private readonly Dictionary<BaseEntity, string> _entityRoom = new Dictionary<BaseEntity, string>();
        private readonly Dictionary<ulong, string> _playerRoom = new Dictionary<ulong, string>();
        private readonly List<BaseEntity> _spawned = new List<BaseEntity>();

        // Tracks which (player, entity) collider pairs we've told PhysX to ignore, purely so
        // "/spike unignore" can put things back without guessing - not needed by the real plugin.
        private readonly Dictionary<ulong, List<(Collider playerCol, Collider entCol)>> _ignoredPairs =
            new Dictionary<ulong, List<(Collider, Collider)>>();

        #endregion

        #region Lifecycle

        void Init()
        {
            permission.RegisterPermission(AdminPermission, this);

            // Reclaim (not kill) anything left over from before a plugin-only reload - this is
            // deliberately the opposite of Unload() below. The whole point of this step is to
            // check whether Physics.IgnoreCollision pairs survive independently of the Oxide/
            // Carbon managed-code lifecycle, which can only be tested if the entity itself is
            // still alive and un-recreated across the reload.
            foreach (var net in BaseNetworkable.serverEntities)
            {
                var ent = net as BaseEntity;
                if (ent == null || ent.IsDestroyed || ent.OwnerID != SpikeOwnerId) continue;
                _spawned.Add(ent);
                _entityRoom[ent] = "A";
            }
            if (_spawned.Count > 0)
                Puts($"[Ludo_RoomsSpike] Reclaimed {_spawned.Count} entity(ies) left over from before this reload (still tagged room A).");
        }

        // Deliberately NOT killing _spawned here (mirrors Ludo_Musika's Unload precedent) - the
        // Phase 0 reload check specifically needs the test wall/box to survive an
        // "o.reload Ludo_RoomsSpike" un-touched, so only Init()'s reclaim logic above, plus this
        // plugin's own "/spike cleanup" command, are allowed to actually kill it.
        void Unload()
        {
            _entityRoom.Clear();
            _ignoredPairs.Clear();
        }

        #endregion

        #region Visibility (mirrors Ludo_Musika's CanNetworkTo exactly, generalized to two hardcoded rooms)

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
        {
            if (entity == null || target == null) return null;
            var be = entity as BaseEntity;
            if (be == null || be == target) return null; // never hide a player from themselves

            string entityRoom = GetEntityRoom(be);
            if (entityRoom == null) return null; // untracked structure/player - no opinion

            string targetRoom = _playerRoom.TryGetValue(target.userID, out var r) ? r : null;
            return entityRoom == targetRoom ? null : (object)false;
        }

        // BasePlayer IS a BaseEntity/BaseNetworkable, so the exact same CanNetworkTo hook that
        // hides our tagged structures also hides players from each other, as long as we can look
        // up a player's own room - which lives in _playerRoom (keyed by userID), not _entityRoom
        // (keyed by entity reference, only used for our spawned structures).
        private string GetEntityRoom(BaseEntity be)
        {
            if (be is BasePlayer bp)
                return _playerRoom.TryGetValue(bp.userID, out var pr) ? pr : null;
            return _entityRoom.TryGetValue(be, out var er) ? er : null;
        }

        // Creates + tags + spawns one entity, matching the "tag before Spawn()" rule everywhere.
        private BaseEntity CreateAndTrack(string prefab, Vector3 pos, Quaternion rot, string room, bool isBuildingBlock)
        {
            var entity = GameManager.server.CreateEntity(prefab, pos, rot);
            if (entity == null) return null;

            if (isBuildingBlock && entity is BuildingBlock block)
            {
                try
                {
                    block.SetGrade(BuildingGrade.Enum.Wood);
                    block.SetHealthToMax();
                }
                catch (Exception ex)
                {
                    Puts($"[Ludo_RoomsSpike] SetGrade failed on raw-spawned wall (expected per plan risk #2), continuing with default grade: {ex.Message}");
                }
            }

            entity.OwnerID = SpikeOwnerId;
            _entityRoom[entity] = room; // tag BEFORE Spawn() - see SpawnWall's original comment
            entity.Spawn();
            _spawned.Add(entity);
            return entity;
        }

        // ABANDONED #1: kill+respawn forced a fresh CanNetworkTo check reliably, but destroys the
        // entity's actual state (items inside a box) on every room switch.
        //
        // ABANDONED #2: player.net.subscriber.Subscribe/Unsubscribe + a hand-crafted
        // Message.Type.EntityDestroy packet. Confirmed via logging that IsSubscribed(group) was
        // False even while the entity was clearly visible, so that bookkeeping isn't what the
        // engine's own distance-based auto-subscription actually uses. The manual destroy packet
        // DID make the entity visually vanish for that one connection, but confirmed via /pref
        // that its BoxColliders were still alive and still blocking movement/getting flagged by
        // Rust's own noclip protection - that packet never touches the server's real subscriber
        // bookkeeping.
        //
        // ABANDONED #3: move the entity 1000m away and back to force a real group leave+rejoin.
        // Should have worked in theory but affects every tracked entity globally on every room
        // switch (not surgical) and was superseded before full re-testing once a proper API
        // turned up.
        //
        // Current approach, found in a real reference plugin (Vanish.cs by Whispers88, its
        // Disappear() method): BaseEntity/BaseNetworkable exposes
        // OnNetworkSubscribersLeave(List<Connection> connections) - an official, targeted API
        // that makes SPECIFIC connections properly forget a specific entity (unlike our earlier
        // guesses, this should update whatever real bookkeeping movement/anti-cheat validation
        // actually consults, not just a cosmetic packet). Vanish also pairs this with
        // player.PauseFlyHackDetection() for player-vanish specifically - confirming anti-cheat
        // really is a separate system with its own real opt-out, not something IgnoreCollision
        // ever could have touched. Vanish's own entity-hide path (DisableEntityPhysics +
        // limitNetworking) is global (collider.enabled=false affects everyone), which doesn't fit
        // our need (room A must still collide with it while room B must not, simultaneously) -
        // OnNetworkSubscribersLeave is the one piece of this that is genuinely per-connection.
        private void HideFrom(BaseEntity entity, BasePlayer player)
        {
            if (entity == null || entity.IsDestroyed || player?.net?.connection == null) return;
            entity.OnNetworkSubscribersLeave(new List<Connection> { player.net.connection });
        }

        // Only affects the ONE player whose room just changed - surgical, not a global refresh.
        // Covers both directions: structures/other players the switcher should now (not) see, AND
        // - since BasePlayer is itself a tracked entity via GetEntityRoom - every OTHER online
        // player's view of the switcher, which needs updating too (e.g. someone in room A who
        // could see this player should stop seeing them the moment they move to room B).
        private void ApplyRoomVisibility(BasePlayer player)
        {
            string myRoom = _playerRoom.TryGetValue(player.userID, out var r) ? r : null;

            foreach (var kv in new List<KeyValuePair<BaseEntity, string>>(_entityRoom))
            {
                var entity = kv.Key;
                if (entity == null || entity.IsDestroyed) continue;
                ApplyPair(entity, kv.Value, player, myRoom);
            }

            foreach (var other in BasePlayer.activePlayerList)
            {
                if (other == null || other == player) continue;
                string otherRoom = _playerRoom.TryGetValue(other.userID, out var or) ? or : null;

                ApplyPair(other, otherRoom, player, myRoom);   // can the switcher see 'other'?
                ApplyPair(player, myRoom, other, otherRoom);   // can 'other' see the switcher now?
            }
        }

        private void ApplyPair(BaseEntity entity, string entityRoom, BasePlayer viewer, string viewerRoom)
        {
            bool shouldSee = entityRoom == viewerRoom;
            Puts($"[Ludo_RoomsSpike] ApplyPair: entity #{entity.net?.ID} room={entityRoom} viewer={viewer.displayName} viewerRoom={viewerRoom} shouldSee={shouldSee}");

            if (shouldSee)
                SendEntity(viewer, entity);
            else
                HideFrom(entity, viewer);
        }

        // Proven manual per-connection push (Trade.cs precedent, ~line 748-757) - used for the
        // "should now see it" side, since a player switching into a room may never have gone
        // through the normal distance-based auto-subscription for this entity.
        private void SendEntity(BasePlayer player, BaseEntity entity)
        {
            if (!Net.sv.IsConnected() || entity.net == null) return;
            var write = Net.sv.StartWrite();
            player.net.connection.validate.entityUpdates++;
            var saveInfo = new BaseNetworkable.SaveInfo { forConnection = player.net.connection, forDisk = false };
            write.PacketID(Message.Type.Entities);
            write.UInt32(player.net.connection.validate.entityUpdates);
            entity.ToStreamForNetwork(write, saveInfo);
            write.Send(new SendInfo(player.net.connection));
        }

        #endregion

        #region Commands

        private bool HasPermission(BasePlayer player) => player != null && permission.UserHasPermission(player.UserIDString, AdminPermission);

        [ChatCommand("spike")]
        void CmdSpike(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player)) { player.ChatMessage("Ludo_RoomsSpike: нет доступа (" + AdminPermission + ")."); return; }

            Action<string> reply = msg => player.ChatMessage(msg);

            if (args.Length == 0)
            {
                reply("Использование: /spike wall|box|join <a|b>|ignore|unignore|layer|unlayer|upgrade|colliders|status|cleanup");
                return;
            }

            switch (args[0].ToLower())
            {
                case "wall": SpawnWall(player, reply); break;
                case "box": SpawnBox(player, reply); break;
                case "join":
                    if (args.Length < 2 || (args[1].ToLower() != "a" && args[1].ToLower() != "b"))
                    { reply("Использование: /spike join <a|b>"); return; }
                    _playerRoom[player.userID] = args[1].ToUpper();
                    ApplyRoomVisibility(player);
                    reply($"Ludo_RoomsSpike: вы теперь в комнате {args[1].ToUpper()}. OnNetworkSubscribersLeave/SendEntity применены just for you.");
                    break;
                case "ignore": DoIgnore(player, reply); break;
                case "unignore": DoUnignore(player, reply); break;
                case "layer": DoAssignLayer(reply); break;
                case "unlayer": DoUnassignLayer(reply); break;
                case "upgrade": DoUpgrade(player, reply); break;
                case "colliders": DoDumpColliders(player, reply); break;
                case "status": DoStatus(player, reply); break;
                case "cleanup": DoCleanup(reply); break;
                default: reply($"Ludo_RoomsSpike: неизвестное действие '{args[0]}'."); break;
            }
        }

        // Deployable, not a BuildingBlock - sidesteps the raw-spawn stability/SetGrade problems
        // entirely (plan risk #2), while still being a real solid structure with its own collider.
        void SpawnWall(BasePlayer player, Action<string> reply)
        {
            const string prefab = "assets/prefabs/building/legacy.shelter.wood/legacy.shelter.wood.deployed.prefab";
            Vector3 pos = player.transform.position;
            Quaternion rot = Quaternion.LookRotation(player.eyes.BodyForward());

            var entity = CreateAndTrack(prefab, pos, rot, "A", isBuildingBlock: false);
            if (entity == null) { reply("Ludo_RoomsSpike: CreateEntity вернул null для legacy.shelter.wood.deployed.prefab."); return; }

            var colliders = entity.GetComponentsInChildren<Collider>(true);
            reply($"Ludo_RoomsSpike: укрытие #{entity.net.ID} заспавнено в комнате A, коллайдеров: {colliders.Length}.");
            LogColliders("shelter", colliders);
        }

        // Fallback if raw BuildingBlock spawning misbehaves - a plain solid deployable with its
        // own collider, to sanity-check the same Physics.IgnoreCollision question in isolation.
        void SpawnBox(BasePlayer player, Action<string> reply)
        {
            const string prefab = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
            Vector3 pos = player.transform.position;
            Quaternion rot = Quaternion.identity;

            var entity = CreateAndTrack(prefab, pos, rot, "A", isBuildingBlock: false);
            if (entity == null) { reply("Ludo_RoomsSpike: CreateEntity вернул null для woodbox_deployed.prefab."); return; }

            var colliders = entity.GetComponentsInChildren<Collider>(true);
            reply($"Ludo_RoomsSpike: ящик #{entity.net.ID} заспавнен в комнате A, коллайдеров: {colliders.Length}.");
            LogColliders("box", colliders);
        }

        // The scenario that actually matters: caller is meant to be the "other room" observer,
        // so we ignore collision between THEIR own movement collider(s) and every tracked
        // spike entity's collider(s) - not between two entities, since the real risk is a
        // player physically colliding with a co-located structure they can't even see.
        void DoIgnore(BasePlayer player, Action<string> reply)
        {
            if (_spawned.Count == 0) { reply("Ludo_RoomsSpike: сначала заспавньте /spike wall или /spike box."); return; }

            var playerColliders = player.GetComponentsInChildren<Collider>(true);
            if (playerColliders.Length == 0) { reply("Ludo_RoomsSpike: у игрока не найдено ни одного Collider (странно, но проверьте лог)."); }
            LogColliders($"player {player.displayName}", playerColliders);

            if (!_ignoredPairs.TryGetValue(player.userID, out var pairs))
                _ignoredPairs[player.userID] = pairs = new List<(Collider, Collider)>();

            int count = 0;
            foreach (var e in _spawned)
            {
                if (e == null || e.IsDestroyed) continue;
                foreach (var entCol in e.GetComponentsInChildren<Collider>(true))
                foreach (var plCol in playerColliders)
                {
                    Physics.IgnoreCollision(plCol, entCol, true);
                    pairs.Add((plCol, entCol));
                    count++;
                }
            }
            reply($"Ludo_RoomsSpike: зарегистрировано {count} пар IgnoreCollision для {player.displayName}. Идите к стене/ящику - должны проходить насквозь.");
        }

        void DoUnignore(BasePlayer player, Action<string> reply)
        {
            if (!_ignoredPairs.TryGetValue(player.userID, out var pairs) || pairs.Count == 0)
            { reply("Ludo_RoomsSpike: для вас нет зарегистрированных ignore-пар."); return; }

            int count = 0;
            foreach (var (plCol, entCol) in pairs)
            {
                if (plCol != null && entCol != null)
                {
                    Physics.IgnoreCollision(plCol, entCol, false);
                    count++;
                }
            }
            pairs.Clear();
            reply($"Ludo_RoomsSpike: снята изоляция для {count} пар. Столкновение должно вернуться к обычному.");
        }

        // Step 1 of the layer-based hypothesis: Physics.IgnoreCollision (per collider pair)
        // doesn't stop CharacterController movement (confirmed - Unity's CharacterController.Move
        // does its own internal sweep that ignores that pairwise setting). Physics.IgnoreLayerCollision
        // operates one level up, on the Layer Collision Matrix, which CharacterController's sweep
        // is expected to respect (it's a broadphase-level filter, not a runtime per-pair exception).
        // This first test is deliberately GLOBAL (every player, not per-room) - just to confirm the
        // core mechanism at all before tackling the harder per-room part (which would need players
        // themselves split across per-room layers too, since IgnoreLayerCollision is world-wide,
        // not per-connection - risky since it means touching the player's own GameObject.layer,
        // which other Rust systems (targeting, hit detection) may assume is the standard value).
        // Reserved1 is confirmed to be a real, mod-safe layer in this exact codebase (used by
        // Vanish.cs's VanishPositionUpdate for its own trigger collider).
        void DoAssignLayer(Action<string> reply)
        {
            if (_spawned.Count == 0) { reply("Ludo_RoomsSpike: сначала заспавньте /spike wall или /spike box."); return; }

            int roomLayer = (int)Layer.Reserved1;
            int playerLayer = LayerMask.NameToLayer("Player (Server)");

            int count = 0;
            foreach (var e in _spawned)
            {
                if (e == null || e.IsDestroyed) continue;
                foreach (var col in e.GetComponentsInChildren<Collider>(true))
                {
                    col.gameObject.layer = roomLayer;
                    count++;
                }
            }

            Physics.IgnoreLayerCollision(roomLayer, playerLayer, true);
            reply($"Ludo_RoomsSpike: {count} коллайдер(ов) переведено на слой Reserved1 ({roomLayer}), IgnoreLayerCollision(Reserved1, Player) = true. Это ГЛОБАЛЬНО для всех игроков - идите внутрь, должны пройти насквозь.");
        }

        void DoUnassignLayer(Action<string> reply)
        {
            int roomLayer = (int)Layer.Reserved1;
            int playerLayer = LayerMask.NameToLayer("Player (Server)");
            Physics.IgnoreLayerCollision(roomLayer, playerLayer, false);
            reply("Ludo_RoomsSpike: IgnoreLayerCollision(Reserved1, Player) отменён. Коллизия должна вернуться (слой у сущностей остался Reserved1 - это не влияет, если сама матрица не игнорит).");
        }

        // Checks whether SetGrade swaps the Collider component out from under an already-
        // registered IgnoreCollision pair - if the logged instance IDs change after this,
        // any ignore-pairs registered before the upgrade have silently stopped applying.
        void DoUpgrade(BasePlayer player, Action<string> reply)
        {
            var block = _spawned.OfType<BuildingBlock>().FirstOrDefault(e => e != null && !e.IsDestroyed);
            if (block == null) { reply("Ludo_RoomsSpike: нет заспавненной стены (BuildingBlock) для апгрейда."); return; }

            LogColliders("wall BEFORE upgrade", block.GetComponentsInChildren<Collider>(true));
            try
            {
                block.SetGrade(BuildingGrade.Enum.Stone);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
            }
            catch (Exception ex)
            {
                Puts($"[Ludo_RoomsSpike] SetGrade failed on raw-spawned wall (expected per plan risk #2): {ex.Message}");
                reply("Ludo_RoomsSpike: апгрейд не сработал (SetGrade упал на сыром спавне - ожидаемо, см. риск #2 плана). Коллайдеры до/после в логе всё равно сравните - компонент мог быть пересоздан другим путём.");
                return;
            }
            LogColliders("wall AFTER upgrade", block.GetComponentsInChildren<Collider>(true));

            reply("Ludo_RoomsSpike: апгрейд до Stone выполнен - сравните instance ID коллайдеров до/после в логе консоли.");
        }

        void DoDumpColliders(BasePlayer player, Action<string> reply)
        {
            foreach (var e in _spawned)
                if (e != null && !e.IsDestroyed)
                    LogColliders($"entity #{e.net.ID}", e.GetComponentsInChildren<Collider>(true));
            LogColliders($"player {player.displayName}", player.GetComponentsInChildren<Collider>(true));
            reply("Ludo_RoomsSpike: дамп коллайдеров написан в консоль сервера.");
        }

        void DoStatus(BasePlayer player, Action<string> reply)
        {
            string room = _playerRoom.TryGetValue(player.userID, out var r) ? r : "(не назначена)";
            int ignoredCount = _ignoredPairs.TryGetValue(player.userID, out var pairs) ? pairs.Count : 0;
            reply($"Ludo_RoomsSpike: ваша комната = {room}; заспавнено сущностей = {_spawned.Count(e => e != null && !e.IsDestroyed)}; активных ignore-пар у вас = {ignoredCount}.");
        }

        void DoCleanup(Action<string> reply)
        {
            foreach (var e in _spawned)
                if (e != null && !e.IsDestroyed) e.Kill();
            _spawned.Clear();
            _entityRoom.Clear();
            _ignoredPairs.Clear();
            reply("Ludo_RoomsSpike: все заспавненные сущности удалены, состояние очищено.");
        }

        void LogColliders(string label, Collider[] colliders)
        {
            Puts($"[Ludo_RoomsSpike] {label}: {colliders.Length} collider(s)");
            foreach (var c in colliders)
                Puts($"[Ludo_RoomsSpike]   - {c.GetType().Name} '{c.gameObject.name}' instanceId={c.GetInstanceID()} isTrigger={c.isTrigger}");
        }

        #endregion
    }
}
