using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    // Реестр "какой игрок/сущность в какой руме". Раньше жил в отдельной
    // Harmony-DLL (HarmonyMods/) — но HarmonyMods грузится на уровне
    // Doorstop, ДО инициализации движка/мира, и патч там реально ронял
    // сервер во время генерации карты (падение подтверждено эмпирически —
    // без мода в HarmonyMods сервер стартует нормально). Патч на AntiHack
    // теперь применяется вручную из Init() этого же плагина — то есть уже
    // после того, как Carbon и весь движок стабильно поднялись, в то же
    // самое безопасное окно, где и другие плагины (Vanish и т.п.) успешно
    // патчат Harmony без крашей.
    public static class BedWarsRoomRegistry
    {
        public static readonly ConcurrentDictionary<ulong, int> PlayerInstance = new ConcurrentDictionary<ulong, int>();
        public static readonly ConcurrentDictionary<ulong, int> EntityInstance = new ConcurrentDictionary<ulong, int>();

        public static void AddPlayer(ulong userId, int instanceId) => PlayerInstance[userId] = instanceId;
        public static void AddEntity(ulong netId, int instanceId) => EntityInstance[netId] = instanceId;
        public static void RemovePlayer(ulong userId) => PlayerInstance.TryRemove(userId, out _);

        public static void RemoveInstance(int instanceId)
        {
            foreach (var kv in PlayerInstance)
                if (kv.Value == instanceId) PlayerInstance.TryRemove(kv.Key, out _);
            foreach (var kv in EntityInstance)
                if (kv.Value == instanceId) EntityInstance.TryRemove(kv.Key, out _);
        }
    }

    // AntiHack.IsColliderBlocking(Collider collider, BasePlayer ply, ...) решает,
    // блокирует ли конкретный коллайдер движение КОНКРЕТНОГО игрока — у Facepunch
    // уже есть per-player точка принятия решения (в отличие от CharacterController/
    // Physics.IgnoreCollision, общих на весь сервер). Патчим её: если коллайдер
    // принадлежит сущности из чужой румы — говорим "не блокирует" только для
    // этого конкретного вызова/игрока.
    [HarmonyPatch(typeof(AntiHack), "IsColliderBlocking")]
    public static class NoClipRoomBypassPatch
    {
        static bool Prefix(Collider collider, BasePlayer ply, ref bool __result)
        {
            try
            {
                if (collider == null || ply == null) return true;

                var entity = collider.GetComponentInParent<BaseEntity>();
                if (entity == null || entity.net == null) return true;

                if (!BedWarsRoomRegistry.EntityInstance.TryGetValue(entity.net.ID.Value, out int entityInst) || entityInst == 0)
                    return true; // не зарегистрированная в BedWars сущность — не наш случай

                BedWarsRoomRegistry.PlayerInstance.TryGetValue(ply.userID, out int playerInst);

                if (entityInst == playerInst)
                    return true; // своя рума — обычная логика анти-чита

                __result = false; // чужая рума — для ЭТОГО игрока коллайдер не блокирует
                return false;
            }
            catch (Exception)
            {
                return true; // при ошибке — безопасное поведение по умолчанию (оригинал)
            }
        }
    }

    // Проверка "хватает ли места для постройки" (DeployVolume.CheckFlags) не
    // связана с AntiHack и не получает игрока напрямую — поэтому запоминаем,
    // кто именно сейчас размещает постройку, в Construction.UpdatePlacement
    // (там игрок есть, в target.player), и используем это ниже.
    [HarmonyPatch(typeof(Construction), "UpdatePlacement")]
    public static class BuildPlacementTrackPatch
    {
        public static ulong CurrentPlacingPlayerId;

        static void Prefix(ref Construction.Target target)
        {
            CurrentPlacingPlayerId = target.player != null ? target.player.userID : 0;
        }
    }

    // Общая точка, через которую проходят все варианты проверки "не мешает ли
    // что-то существующее размещению" (сфера/капсула/OBB/bounds — все зовут
    // именно CheckFlags). Убираем из списка кандидатов коллайдеры сущностей
    // из ЧУЖОЙ по отношению к текущему строителю румы ДО того, как оригинал
    // их обработает — тогда постройка в чужой руме просто не мешает.
    [HarmonyPatch(typeof(DeployVolume), "CheckFlags")]
    public static class BuildOverlapBypassPatch
    {
        static void Prefix(List<Collider> list)
        {
            if (list == null || list.Count == 0) return;

            ulong placerId = BuildPlacementTrackPatch.CurrentPlacingPlayerId;
            if (placerId == 0) return;

            if (!BedWarsRoomRegistry.PlayerInstance.TryGetValue(placerId, out int placerInst) || placerInst == 0)
                return;

            list.RemoveAll(collider =>
            {
                if (collider == null) return false;
                var entity = collider.GetComponentInParent<BaseEntity>();
                if (entity == null || entity.net == null) return false;
                if (!BedWarsRoomRegistry.EntityInstance.TryGetValue(entity.net.ID.Value, out int entInst) || entInst == 0) return false;
                return entInst != placerInst; // чужая рума — не мешает строить
            });
        }
    }

    // BuildingProximity.Check находит соседние BuildingBlock через
    // Vis.Entities<BuildingBlock>(...) СОВСЕМ МИМО DeployVolume/CheckFlags —
    // отдельная система "слишком близко к чужому фундаменту/нельзя
    // подключиться". Vis.Entities<T> — generic-метод, атрибутом его не
    // пропатчить (нужен закрытый generic-инстанс), патчим вручную в Init().
    // Работает только пока идёт активное размещение (CurrentPlacingPlayerId
    // выставлен из BuildPlacementTrackPatch), чтобы не трогать все остальные
    // вызовы Vis.Entities<BuildingBlock> в игре.
    public static class VisEntitiesBuildingFilterPatch
    {
        public static void Postfix(List<BuildingBlock> list)
        {
            ulong placerId = BuildPlacementTrackPatch.CurrentPlacingPlayerId;
            if (placerId == 0 || list == null || list.Count == 0) return;

            if (!BedWarsRoomRegistry.PlayerInstance.TryGetValue(placerId, out int placerInst) || placerInst == 0)
                return;

            list.RemoveAll(block =>
            {
                if (block == null || block.net == null) return false;
                if (!BedWarsRoomRegistry.EntityInstance.TryGetValue(block.net.ID.Value, out int entInst) || entInst == 0) return false;
                return entInst != placerInst; // чужая рума — не мешает строить
            });
        }
    }

    [Info("BedWars", "BedWarsTeam", "1.0.0")]
    [Description("BedWars instancing — network isolation and game logic")]
    public class BedWars : RustPlugin
    {
        Harmony _harmony;

        // ═══════════════════════════════════════════════════════
        //  Типы данных
        // ═══════════════════════════════════════════════════════

        enum TeamColor { Red, Blue, Green, Yellow }

        class Team
        {
            public TeamColor Color;
            public HashSet<ulong> Players = new();
            public Vector3 Spawn;
            public BaseEntity Bed;
            public bool BedAlive = true;
        }

        class GameInstance
        {
            public int Id;
            public HashSet<ulong> Players = new();
            public List<ulong> EntityNetIds = new();
            public Dictionary<TeamColor, Team> Teams = new();
            public bool Active;
        }

        readonly Dictionary<int, GameInstance> _instances = new();
        int _nextId = 1;
        Timer _debugTimer;

        // Временно выключено по запросу — верни true и перезагрузи плагин,
        // если снова понадобится трассировка join/leave/registration в лог.
        const bool DebugLoggingEnabled = false;

        void Init()
        {
            // Хук дорогой (дёргается на каждую пару энтити↔игрок),
            // подписываемся только пока есть хоть один активный инстанс.
            Unsubscribe(nameof(CanNetworkTo));
            if (DebugLoggingEnabled)
                _debugTimer = timer.Repeat(2f, 0, DebugLogState);

            // Применяем Harmony-патч сами, здесь и сейчас — уже после того,
            // как Carbon и движок полностью поднялись (в отличие от прежнего
            // варианта через HarmonyMods/, который грузился на уровне
            // Doorstop ДО инициализации мира и ронял сервер).
            _harmony = new Harmony("com.bedwars.noclipbypass");
            _harmony.PatchAll(typeof(NoClipRoomBypassPatch).Assembly);

            // Vis.Entities<T> — generic, атрибутом [HarmonyPatch] не патчится,
            // нужен закрытый generic-метод именно для T = BuildingBlock.
            var visEntitiesOpen = typeof(Vis).GetMethods()
                .FirstOrDefault(m => m.Name == "Entities" && m.IsGenericMethodDefinition &&
                                      m.GetParameters().Length == 5 &&
                                      m.GetParameters()[0].ParameterType == typeof(Vector3) &&
                                      m.GetParameters()[1].ParameterType == typeof(float));
            if (visEntitiesOpen != null)
            {
                var visEntitiesClosed = visEntitiesOpen.MakeGenericMethod(typeof(BuildingBlock));
                _harmony.Patch(visEntitiesClosed, postfix: new HarmonyMethod(
                    typeof(VisEntitiesBuildingFilterPatch).GetMethod(nameof(VisEntitiesBuildingFilterPatch.Postfix))));
            }
        }

        // ─── Отладочное логирование (позиция игроков в инстансах) ─
        void DebugLogState()
        {
            if (!DebugLoggingEnabled) return;
            foreach (var kv in BedWarsRoomRegistry.PlayerInstance)
            {
                var p = BasePlayer.FindByID(kv.Key);
                if (p == null || !p.IsConnected) continue;
                Puts($"[BW-DEBUG] player={p.displayName} instance={kv.Value} pos={p.transform.position}");
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Управление инстансами
        // ═══════════════════════════════════════════════════════

        GameInstance CreateInstance()
        {
            var inst = new GameInstance { Id = _nextId++ };
            _instances[inst.Id] = inst;
            if (_instances.Count == 1) Subscribe(nameof(CanNetworkTo));
            return inst;
        }

        void JoinInstance(BasePlayer player, GameInstance instance)
        {
            LeaveInstance(player); // всегда выходим из старой румы перед входом в новую

            instance.Players.Add(player.userID);
            BedWarsRoomRegistry.AddPlayer(player.userID, instance.Id);
            SetupPhysicsIsolation(player, instance.Id);

            // Без этого CanNetworkTo не переоценится для уже "подписанных" сущностей —
            // движок не переспрашивает видимость сам по себе просто от смены значения в словаре.
            RefreshInstanceEntities(instance.Id);

            if (DebugLoggingEnabled) Puts($"[BW-DEBUG] {player.displayName} JOIN instance={instance.Id} pos={player.transform.position}");
        }

        void LeaveInstance(BasePlayer player)
        {
            BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int instId);
            if (instId == 0) return;

            if (_instances.TryGetValue(instId, out var inst))
                inst.Players.Remove(player.userID);

            BedWarsRoomRegistry.RemovePlayer(player.userID);

            if (DebugLoggingEnabled) Puts($"[BW-DEBUG] {player.displayName} LEAVE instance={instId} pos={player.transform.position}");

            // Форсим переоценку видимости построек старой румы для этого игрока
            RefreshInstanceEntities(instId);
        }

        // ─── Принудительное обновление сетевой видимости ──────
        // Toggle limitNetworking заставляет сущность полностью выйти и
        // перевойти в network group — это единственный способ заставить
        // Rust заново вызвать CanNetworkTo для уже подписанных наблюдателей.
        void RefreshInstanceEntities(int instanceId)
        {
            if (!_instances.TryGetValue(instanceId, out var inst)) return;
            foreach (var netId in inst.EntityNetIds)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(netId)) as BaseEntity;
                if (ent != null) ForceNetworkRefresh(ent);
            }
        }

        void ForceNetworkRefresh(BaseEntity entity)
        {
            if (entity?.net == null) return;
            bool wasLimited = entity.limitNetworking;
            entity.limitNetworking = true;
            entity.UpdateNetworkGroup();
            entity.limitNetworking = wasLimited;
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
        }

        // ─── Регистрация сущности арены ────────────────────────
        void RegisterEntity(BaseEntity entity, int instanceId)
        {
            if (entity?.net == null) return;
            if (BedWarsRoomRegistry.EntityInstance.ContainsKey(entity.net.ID.Value)) return; // уже зарегистрирована

            BedWarsRoomRegistry.AddEntity(entity.net.ID.Value, instanceId);

            if (_instances.TryGetValue(instanceId, out var inst))
                inst.EntityNetIds.Add(entity.net.ID.Value);

            // Изолируем физику сущности от игроков других инстансов
            foreach (var other in _instances.Values)
            {
                if (other.Id == instanceId) continue;
                foreach (var uid in other.Players)
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null) IsolateEntityFromPlayer(entity, p);
                }
            }

            entity.SendNetworkUpdateImmediate();

            if (DebugLoggingEnabled) Puts($"[BW-DEBUG] Registered entity={entity.ShortPrefabName} netId={entity.net.ID.Value} instance={instanceId} pos={entity.transform.position}");
        }

        // ─── Автоматическая регистрация построек/деплоаблов ────
        // Срабатывает на любую постройку/деплоаблу, поставленную молотком
        // (фундаменты, стены, сундуки, печи и т.д.) — ставим её в тот же
        // инстанс, что и у игрока-строителя.
        void OnEntityBuilt(Planner plan, GameObject go)
        {
            var entity = go.ToBaseEntity();
            if (entity == null) return;

            var builder = plan.GetOwnerPlayer();
            if (builder == null) return;

            BedWarsRoomRegistry.PlayerInstance.TryGetValue(builder.userID, out int instId);
            if (instId == 0) return; // строитель вне инстанса — обычная постройка на сервере

            RegisterEntity(entity, instId);
        }

        // ─── Физическая изоляция ───────────────────────────────
        void SetupPhysicsIsolation(BasePlayer newPlayer, int myInstanceId)
        {
            foreach (var inst in _instances.Values)
            {
                if (inst.Id == myInstanceId) continue;

                // Сущности чужих инстансов
                foreach (var netId in inst.EntityNetIds)
                {
                    var ent = BaseNetworkable.serverEntities.Find(
                        new NetworkableId(netId)) as BaseEntity;
                    if (ent != null) IsolateEntityFromPlayer(ent, newPlayer);
                }

                // Физика игрок ↔ игрок между инстансами
                foreach (var uid in inst.Players)
                {
                    var other = BasePlayer.FindByID(uid);
                    if (other == null) continue;
                    IsolatePlayerFromPlayer(newPlayer, other);
                }
            }
        }

        void IsolateEntityFromPlayer(BaseEntity entity, BasePlayer player)
        {
            foreach (var ec in entity.GetComponentsInChildren<Collider>())
                foreach (var pc in player.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(ec, pc, true);
        }

        void IsolatePlayerFromPlayer(BasePlayer a, BasePlayer b)
        {
            foreach (var ca in a.GetComponentsInChildren<Collider>())
                foreach (var cb in b.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(ca, cb, true);
        }

        // ─── Конец инстанса ────────────────────────────────────
        void EndInstance(int instanceId)
        {
            if (!_instances.TryGetValue(instanceId, out var inst)) return;

            // Сносим все постройки/деплоаблы арены — иначе они останутся в мире навсегда
            foreach (var netId in inst.EntityNetIds)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(netId)) as BaseEntity;
                if (ent != null && !ent.IsDestroyed) ent.Kill();
            }

            BedWarsRoomRegistry.RemoveInstance(instanceId);
            _instances.Remove(instanceId);

            if (_instances.Count == 0) Unsubscribe(nameof(CanNetworkTo));
        }

        // ─── Сетевая изоляция построек и игроков между инстансами ─
        // Работает для ЛЮБОГО BaseNetworkable — стены, сундуки, кровати,
        // не только игроки. Возврат false = скрыть от target, null = как обычно.
        object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
        {
            if (entity == null || target == null) return null;

            int entityInst = entity is BasePlayer ep
                ? GetPlayerInstance(ep.userID)
                : (entity.net != null ? GetEntityInstance(entity.net.ID.Value) : 0);

            if (entityInst == 0) return null; // не в системе инстансов — не трогаем

            int targetInst = GetPlayerInstance(target.userID);
            return entityInst == targetInst ? (object)null : false;
        }

        int GetPlayerInstance(ulong userId)
        {
            BedWarsRoomRegistry.PlayerInstance.TryGetValue(userId, out int inst);
            return inst;
        }

        int GetEntityInstance(ulong netId)
        {
            BedWarsRoomRegistry.EntityInstance.TryGetValue(netId, out int inst);
            return inst;
        }

        // ═══════════════════════════════════════════════════════
        //  Oxide хуки
        // ═══════════════════════════════════════════════════════

        // ─── Урон ──────────────────────────────────────────────
        // Важно: victim может быть не только игроком, но и постройкой/кроватью
        // (BuildingBlock, StorageContainer и т.п.) — она тоже должна быть
        // привязана к инстансу через EntityInstance, иначе урон по чужим
        // постройкам либо не блокируется, либо блокируется всегда (по кроватям
        // в своём же инстансе тоже).
        object OnEntityTakeDamage(BaseCombatEntity victim, HitInfo info)
        {
            var attacker = info?.InitiatorPlayer;
            if (attacker == null) return null;

            int attackerInst = GetPlayerInstance(attacker.userID);

            int victimInst = victim is BasePlayer vp
                ? GetPlayerInstance(vp.userID)
                : (victim.net != null ? GetEntityInstance(victim.net.ID.Value) : 0);

            if (attackerInst == 0 && victimInst == 0) return null; // оба вне системы инстансов
            if (victimInst != 0 && attackerInst != victimInst) return true; // чужой инстанс — отменяем

            return null;
        }

        // ─── Лут ───────────────────────────────────────────────
        object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (container?.net == null) return null;

            BedWarsRoomRegistry.EntityInstance.TryGetValue(container.net.ID.Value, out int entInst);
            BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int pInst);

            if (entInst != 0 && pInst != entInst)
                return false;

            return null;
        }

        // ─── Чат ───────────────────────────────────────────────
        object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int myInst);
            if (myInst == 0) return null;

            if (!_instances.TryGetValue(myInst, out var inst)) return null;

            foreach (var uid in inst.Players)
            {
                var p = BasePlayer.FindByID(uid);
                p?.SendConsoleCommand("chat.add", 2, player.userID,
                    $"<color=#55aaff>{player.displayName}</color>: {message}");
            }

            return true; // блокируем глобальный чат
        }

        // ─── Отключение игрока ─────────────────────────────────
        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            LeaveInstance(player);
        }

        // ─── Смерть игрока ─────────────────────────────────────
        void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int instId);
            if (instId == 0) return;

            if (!_instances.TryGetValue(instId, out var inst)) return;

            // Респавн через 5 секунд (если кровать жива)
            var team = GetPlayerTeam(player.userID, inst);
            if (team != null && team.BedAlive)
                timer.Once(5f, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    player.Respawn();
                    MoveToSpawn(player, team.Spawn);
                });
        }

        // ═══════════════════════════════════════════════════════
        //  Команды
        // ═══════════════════════════════════════════════════════

        [ChatCommand("bw")]
        void CmdBedWars(BasePlayer player, string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                player.ChatMessage("<color=#ffcc00>BedWars команды:</color>\n" +
                    "/bw join <id> — войти в инстанс\n" +
                    "/bw leave     — выйти из инстанса\n" +
                    "/bw new       — создать новый инстанс\n" +
                    "/bw list      — список инстансов\n" +
                    "/bw info      — твой текущий инстанс");
                return;
            }

            switch (args[0].ToLower())
            {
                case "new":
                    var inst = CreateInstance();
                    JoinInstance(player, inst);
                    player.ChatMessage($"Создан инстанс <color=#55ff55>#{inst.Id}</color>. Ты в нём.");
                    break;

                case "join":
                    if (args.Length < 2 || !int.TryParse(args[1], out int joinId))
                    {
                        player.ChatMessage("Используй: /bw join <id>");
                        return;
                    }
                    if (!_instances.TryGetValue(joinId, out var target))
                    {
                        player.ChatMessage("Инстанс не найден.");
                        return;
                    }
                    LeaveInstance(player);
                    JoinInstance(player, target);
                    player.ChatMessage($"Ты в инстансе <color=#55ff55>#{joinId}</color>. Игроков: {target.Players.Count}");
                    break;

                case "leave":
                    BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int curInst);
                    if (curInst == 0)
                    {
                        player.ChatMessage("Ты не в инстансе.");
                        return;
                    }
                    LeaveInstance(player);
                    player.ChatMessage("Ты вышел из инстанса.");
                    break;

                case "list":
                    if (_instances.Count == 0)
                    {
                        player.ChatMessage("Нет активных инстансов.");
                        return;
                    }
                    var sb = new System.Text.StringBuilder("<color=#ffcc00>Инстансы:</color>\n");
                    foreach (var kv in _instances)
                        sb.AppendLine($"  #{kv.Key} — игроков: {kv.Value.Players.Count}");
                    player.ChatMessage(sb.ToString());
                    break;

                case "info":
                    BedWarsRoomRegistry.PlayerInstance.TryGetValue(player.userID, out int myInst);
                    player.ChatMessage(myInst == 0
                        ? "Ты не в инстансе."
                        : $"Твой инстанс: <color=#55ff55>#{myInst}</color>");
                    break;
            }
        }

        // Команды для администраторов
        [ConsoleCommand("bw.end")]
        void CcEndInstance(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (!arg.HasArgs() || !int.TryParse(arg.GetString(0), out int id))
            {
                arg.ReplyWith("bw.end <instanceId>");
                return;
            }
            if (!_instances.ContainsKey(id))
            {
                arg.ReplyWith("Instance not found.");
                return;
            }
            EndInstance(id);
            arg.ReplyWith($"Instance #{id} ended.");
        }

        [ConsoleCommand("bw.listall")]
        void CcListAll(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (_instances.Count == 0) { arg.ReplyWith("No active instances."); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _instances)
                sb.AppendLine($"Instance #{kv.Key}: {kv.Value.Players.Count} players, {kv.Value.EntityNetIds.Count} entities");
            arg.ReplyWith(sb.ToString());
        }

        // ═══════════════════════════════════════════════════════
        //  ЭКСПЕРИМЕНТ: проверка CharacterController.excludeLayers
        //  Гипотеза: раз CharacterController наследуется от Collider,
        //  excludeLayers у него тоже per-instance свойство — можно
        //  избирательно выключить коллизию с конкретным слоем ИМЕННО
        //  для одного игрока, не трогая остальных. Не подтверждено,
        //  что внутренний sweep движения его реально учитывает —
        //  команды ниже нужны только чтобы это живьём проверить.
        // ═══════════════════════════════════════════════════════

        // 1. Смотрим, какие индексы слоёв (0-31) в этой сборке вообще
        //    не именованы — их безопаснее всего занять под тест, чтобы
        //    случайно не сломать существующую игровую логику коллизий.
        [ConsoleCommand("bw.layers")]
        void CcListLayers(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                sb.AppendLine($"{i}: {(string.IsNullOrEmpty(name) ? "(unnamed - кандидат для теста)" : name)}");
            }
            arg.ReplyWith(sb.ToString());
        }

        // 2. Ставим тестовый слой на объект, на который смотрит игрок
        //    (например, на стену BedWars-арены).
        [ConsoleCommand("bw.testobject.setlayer")]
        void CcSetTestObjectLayer(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("Запускать из игры (F1), не из серверной консоли."); return; }
            if (!arg.HasArgs() || !int.TryParse(arg.GetString(0), out int layerIndex) || layerIndex < 0 || layerIndex > 31)
            {
                arg.ReplyWith("bw.testobject.setlayer <0-31>");
                return;
            }

            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.position, player.eyes.BodyForward(), out hit, 10f))
            {
                arg.ReplyWith("Не смотришь ни на что в пределах 10м.");
                return;
            }

            SetLayerRecursive(hit.collider.gameObject, layerIndex);
            arg.ReplyWith($"Слой {layerIndex} установлен на {hit.collider.gameObject.name} (и дочерние объекты).");
        }

        void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        // 3. Включаем/выключаем exclude для этого слоя у СВОЕГО же
        //    CharacterController — если механизм работает, после этого
        //    игрок должен пройти сквозь объект из шага 2 без коллизии,
        //    при этом коллизия с остальным миром должна остаться нормальной.
        [ConsoleCommand("bw.testexclude")]
        void CcTestExclude(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("Запускать из игры (F1), не из серверной консоли."); return; }
            if (!arg.HasArgs() || !int.TryParse(arg.GetString(0), out int layerIndex) || layerIndex < 0 || layerIndex > 31)
            {
                arg.ReplyWith("bw.testexclude <0-31>");
                return;
            }

            var cc = player.GetComponent<CharacterController>();
            if (cc == null)
            {
                arg.ReplyWith("У игрока нет компонента CharacterController — этот подход напрямую не сработает, нужен другой путь (Harmony-патч движения).");
                return;
            }

            int mask = 1 << layerIndex;
            bool currentlyExcluded = (cc.excludeLayers.value & mask) != 0;
            if (currentlyExcluded)
                cc.excludeLayers = new LayerMask { value = cc.excludeLayers.value & ~mask };
            else
                cc.excludeLayers = new LayerMask { value = cc.excludeLayers.value | mask };

            arg.ReplyWith($"excludeLayers для слоя {layerIndex}: {(!currentlyExcluded ? "ВКЛЮЧЕН (должен проходить сквозь)" : "выключен (обычная коллизия)")}. Иди проверяй на объекте из bw.testobject.setlayer.");
        }

        // ═══════════════════════════════════════════════════════
        //  Вспомогательные методы
        // ═══════════════════════════════════════════════════════

        Team GetPlayerTeam(ulong userId, GameInstance inst)
        {
            foreach (var team in inst.Teams.Values)
                if (team.Players.Contains(userId))
                    return team;
            return null;
        }

        void MoveToSpawn(BasePlayer player, Vector3 pos)
        {
            player.Teleport(pos);
        }

        // ─── Очистка при выгрузке плагина ─────────────────────
        void Unload()
        {
            _debugTimer?.Destroy();
            _harmony?.UnpatchAll(_harmony.Id);
            foreach (var id in new List<int>(_instances.Keys))
                EndInstance(id);
        }
    }
}
