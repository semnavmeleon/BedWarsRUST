using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Ludo_RoomsSpike2", "Semnavmeleon", "0.1.0")]
    [Description("Throwaway spike testing whether the occupied-but-decorative Unity layers Bush(26)/Clutter(25)/Physics Debris(31) can be safely repurposed as extra structure/player layer pairs for Ludo_Rooms, beyond the 3 confirmed-free layers (3,6,7) which only cover one fully isolated room. Two separate risks under test: (1) does the same structure+player layer trick that worked for layer 3/6 also work mechanically on these; (2) does toggling Physics.IgnoreLayerCollision on them visibly disturb real bushes/clutter/debris elsewhere on the map, since that rule is global and not scoped to our own spawned test objects. Delete once validated either way.")]
    public class Ludo_RoomsSpike2 : RustPlugin
    {
        #region Fields

        private const string AdminPermission = "ludoroomsspike2.admin";
        private const ulong SpikeOwnerId = 999999999900000002UL; // distinct range from Ludo_Rooms' RoomOwnerIdBase and the original Ludo_RoomsSpike's (...900000001)

        // name -> raw layer index, exactly the 3 candidates flagged as lowest-apparent-risk out of
        // all 29 occupied layers (everything else was either clearly load-bearing by name - Default,
        // Terrain, Construction, Deployed, Trigger, Vehicle*, Water, AI - or ambiguous enough to skip
        // testing at all). Accessible by number too, for anything not in this map.
        private static readonly Dictionary<string, int> CandidateNames = new Dictionary<string, int>
        {
            { "bush", 26 },
            { "clutter", 25 },
            { "debris", 31 },
        };

        private readonly List<BaseEntity> _spawned = new List<BaseEntity>();

        // Every (layerA, layerB, wasIgnored) pair this spike has ever set, so Unload() can put
        // every single one back to its original state - a stray IgnoreLayerCollision left set after
        // this plugin is gone would keep silently affecting every real bush/clutter/debris prop on
        // the whole map, not just our own test objects.
        private readonly List<(int a, int b)> _activeIgnoreRules = new List<(int, int)>();

        private int _standardPlayerLayer = -1;
        private int? _playerOriginalLayer; // the one real player this spike ever moves, so /spike2 unlayer can restore it precisely

        #endregion

        #region Lifecycle

        private bool HasPermission(BasePlayer player) => player != null && permission.UserHasPermission(player.UserIDString, AdminPermission);

        void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
            _standardPlayerLayer = LayerMask.NameToLayer("Player (Server)");
        }

        // Deliberately reverts EVERYTHING regardless of what state testing was left in - this spike
        // existing at all is inherently a "might be actively lying about the state of the game
        // world" situation, so unload must be unconditionally safe to call at any point.
        void Unload()
        {
            foreach (var (a, b) in _activeIgnoreRules)
                Physics.IgnoreLayerCollision(a, b, false);
            _activeIgnoreRules.Clear();

            foreach (var e in _spawned)
                if (e != null && !e.IsDestroyed) e.Kill();
            _spawned.Clear();
        }

        #endregion

        #region Commands

        [ChatCommand("spike2")]
        void Cmd(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player)) { player.ChatMessage("Ludo_RoomsSpike2: нет доступа (" + AdminPermission + ")."); return; }
            Action<string> reply = msg => player.ChatMessage(msg);

            if (args.Length == 0)
            {
                reply("Использование: /spike2 wall|box <слой> | enable <слой> | disable <слой> | playerlayer <слой> | playerunlayer | exclude on|off | status | cleanup\nСлои: bush, clutter, debris (или номер).");
                return;
            }

            switch (args[0].ToLower())
            {
                case "wall": SpawnTest(player, args, isWall: true, reply); break;
                case "box": SpawnTest(player, args, isWall: false, reply); break;
                case "enable": SetIgnoreRule(player, args, true, reply); break;
                case "disable": SetIgnoreRule(player, args, false, reply); break;
                case "playerlayer": DoAssignPlayerLayer(player, args, reply); break;
                case "playerunlayer": DoUnassignPlayerLayer(player, reply); break;
                case "exclude": SetExcludeLayers(args, reply); break;
                case "status": DoStatus(reply); break;
                case "cleanup": DoCleanup(reply); break;
                default: reply($"Ludo_RoomsSpike2: неизвестное действие '{args[0]}'."); break;
            }
        }

        private bool ResolveLayer(string token, Action<string> reply, out int layer)
        {
            layer = -1;
            if (token == null) { reply("Укажите слой: bush, clutter, debris или номер."); return false; }
            if (CandidateNames.TryGetValue(token.ToLower(), out layer)) return true;
            if (int.TryParse(token, out layer)) return true;
            reply($"Ludo_RoomsSpike2: неизвестный слой '{token}'. Доступно: bush, clutter, debris, или число 0-31.");
            return false;
        }

        // Deployable, not a raw BuildingBlock - sidesteps the SetGrade/collider-timing issues the
        // original Ludo_RoomsSpike and Ludo_Rooms itself both hit with raw building block spawns,
        // since that's not what's under test here (that bug is already understood and fixed
        // elsewhere - this spike is purely about the layer, not about spawn timing).
        private void SpawnTest(BasePlayer player, string[] args, bool isWall, Action<string> reply)
        {
            if (args.Length < 2 || !ResolveLayer(args[1], reply, out var layer)) return;

            string prefab = isWall
                ? "assets/prefabs/building/legacy.shelter.wood/legacy.shelter.wood.deployed.prefab"
                : "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
            Vector3 pos = player.transform.position;
            Quaternion rot = isWall ? Quaternion.LookRotation(player.eyes.BodyForward()) : Quaternion.identity;

            var entity = GameManager.server.CreateEntity(prefab, pos, rot);
            if (entity == null) { reply($"Ludo_RoomsSpike2: CreateEntity вернул null для {prefab}."); return; }

            entity.OwnerID = SpikeOwnerId;
            entity.Spawn();
            foreach (var col in entity.GetComponentsInChildren<Collider>(true))
                col.gameObject.layer = layer;
            _spawned.Add(entity);

            reply($"Ludo_RoomsSpike2: {(isWall ? "укрытие" : "ящик")} #{entity.net.ID} заспавнен на слое {layer} ({LayerMask.LayerToName(layer)}). Сейчас должен быть твёрдым (никаких ignore-правил ещё не включено) - подойдите, проверьте, что упираетесь.");
        }

        // The core mechanic under test: does IgnoreLayerCollision(candidate, standardPlayerLayer,
        // true) make our test object pass-through-able for a standard-layer player, exactly like it
        // did for layer 3 in the real plugin - and, separately and just as importantly, walk around
        // to any REAL bushes/clutter/debris on the map while this is active and see if anything
        // about them changed (visually, or whether you now walk through real ones too).
        private void SetIgnoreRule(BasePlayer player, string[] args, bool ignore, Action<string> reply)
        {
            if (args.Length < 2 || !ResolveLayer(args[1], reply, out var layer)) return;

            Physics.IgnoreLayerCollision(layer, _standardPlayerLayer, ignore);
            if (ignore && !_activeIgnoreRules.Contains((layer, _standardPlayerLayer)))
                _activeIgnoreRules.Add((layer, _standardPlayerLayer));
            else if (!ignore)
                _activeIgnoreRules.RemoveAll(r => r.a == layer && r.b == _standardPlayerLayer);

            reply($"Ludo_RoomsSpike2: IgnoreLayerCollision({layer} [{LayerMask.LayerToName(layer)}], Player (Server), {ignore}) применено.\n" +
                  (ignore
                      ? "Тестовый объект должен стать проходимым. Также сходите к настоящим кустам/мусору/обломкам на карте - не изменилось ли что-то у НИХ (это глобальное правило, не только для наших объектов)."
                      : "Правило снято, коллизия должна вернуться к обычной."));
        }

        // Tests Collider.excludeLayers (confirmed to exist in this exact build via get_/set_
        // excludeLayers found in UnityEngine.PhysicsModule.dll) as a possible PER-OBJECT
        // alternative to the whole-layer IgnoreLayerCollision matrix - if this actually affects
        // CharacterController.Move()'s own sweep (unconfirmed - Physics.IgnoreCollision, the other
        // per-pair API, does NOT, per the original Ludo_RoomsSpike's test), it would mean we don't
        // need to repurpose occupied layers at all: just mark our own room's structures to exclude
        // the standard player layer, one object at a time, with zero effect on anything else in the
        // world. Deliberately does NOT touch Physics.IgnoreLayerCollision at all, so a positive
        // result here is unambiguous - it can only be this API, not a leftover global rule.
        private void SetExcludeLayers(string[] args, Action<string> reply)
        {
            var entity = _spawned.LastOrDefault(e => e != null && !e.IsDestroyed);
            if (entity == null) { reply("Ludo_RoomsSpike2: сначала заспавньте /spike2 wall|box <слой>."); return; }

            bool enable = args.Length < 2 || args[1].ToLower() != "off";
            LayerMask mask = enable ? (LayerMask)(1 << _standardPlayerLayer) : (LayerMask)0;

            foreach (var col in entity.GetComponentsInChildren<Collider>(true))
                col.excludeLayers = mask;

            reply($"Ludo_RoomsSpike2: excludeLayers для последнего объекта (#{entity.net.ID}) {(enable ? "включён на Player (Server)" : "выключен")} - глобальная IgnoreLayerCollision матрица НЕ трогалась. Подойдите: если стали проходить сквозь объект - это per-объектный способ и работает для движения игрока, если нет - CharacterController его тоже игнорирует, как и Physics.IgnoreCollision.");
        }

        // Second half of the real mechanic: moving the PLAYER's own collider onto a candidate layer
        // (paired with a different candidate as that "room's" structure layer) - the actual risk
        // here isn't the test wall, it's whether moving the player onto e.g. Bush's layer makes them
        // behave oddly against everything ELSE already on that layer (real bushes) or against
        // terrain/other systems that key off layer. Only ever touches the calling player's own
        // colliders, and only one player at a time (single _playerOriginalLayer slot) - this is a
        // spike, not meant to support multiple simultaneous testers.
        private void DoAssignPlayerLayer(BasePlayer player, string[] args, Action<string> reply)
        {
            if (args.Length < 2 || !ResolveLayer(args[1], reply, out var layer)) return;

            var colliders = player.GetComponentsInChildren<Collider>(true);
            if (_playerOriginalLayer == null && colliders.Length > 0)
                _playerOriginalLayer = colliders[0].gameObject.layer;

            foreach (var col in colliders)
                col.gameObject.layer = layer;

            reply($"Ludo_RoomsSpike2: ваши коллайдеры переведены на слой {layer} ({LayerMask.LayerToName(layer)}). Походите по обычной территории - трава/террейн/другие игроки должны продолжать работать как обычно. /spike2 playerunlayer чтобы вернуть.");
        }

        private void DoUnassignPlayerLayer(BasePlayer player, Action<string> reply)
        {
            if (_playerOriginalLayer == null) { reply("Ludo_RoomsSpike2: слой игрока не менялся."); return; }
            foreach (var col in player.GetComponentsInChildren<Collider>(true))
                col.gameObject.layer = _playerOriginalLayer.Value;
            reply($"Ludo_RoomsSpike2: слой игрока возвращён на {_playerOriginalLayer.Value} ({LayerMask.LayerToName(_playerOriginalLayer.Value)}).");
            _playerOriginalLayer = null;
        }

        private void DoStatus(Action<string> reply)
        {
            var lines = new List<string> { "Ludo_RoomsSpike2 - статус:" };
            lines.Add($"Заспавнено тестовых объектов: {_spawned.Count(e => e != null && !e.IsDestroyed)}");
            lines.Add(_activeIgnoreRules.Count == 0
                ? "Активных ignore-правил: нет"
                : "Активные ignore-правила: " + string.Join(", ", _activeIgnoreRules.Select(r => $"({r.a},{r.b})")));
            lines.Add(_playerOriginalLayer == null ? "Слой игрока: не изменён" : $"Слой игрока временно изменён (исходный: {_playerOriginalLayer.Value})");
            reply(string.Join("\n", lines));
        }

        private void DoCleanup(Action<string> reply)
        {
            int killed = _spawned.Count(e => e != null && !e.IsDestroyed);
            foreach (var e in _spawned)
                if (e != null && !e.IsDestroyed) e.Kill();
            _spawned.Clear();

            int reverted = _activeIgnoreRules.Count;
            foreach (var (a, b) in _activeIgnoreRules)
                Physics.IgnoreLayerCollision(a, b, false);
            _activeIgnoreRules.Clear();

            reply($"Ludo_RoomsSpike2: убрано {killed} тестовых объектов, снято {reverted} ignore-правил. Слой игрока не тронут (если менялся - используйте /spike2 playerunlayer отдельно).");
        }

        #endregion
    }
}
