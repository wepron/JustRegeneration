using HarmonyLib;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;

namespace JustRegeneration
{
    // =========================================================================
    // 1. ПАТЧИ (God Mode и Age)
    // =========================================================================

    [HarmonyPatch(typeof(AgentApplyDamageModel), "CalculateDamage")]
    public static class Patch_CalculateDamage
    {
        private static bool _patchedOnce = false;

        static bool Prefix(ref float __result, in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            try
            {
                if (!_patchedOnce)
                {
                    _patchedOnce = true;
                    TextObject msg = new TextObject("{=JR_GodModePatchActive}Just Regeneration: God Mode patch (CalculateDamage) is active!");
                    if (Campaign.Current != null)
                        InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                }

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return true;

                Agent victim = attackInformation.VictimAgent;
                if (victim == null)
                    return true;

                bool isVictimPlayerControlled = victim.IsPlayerControlled;
                if (victim.IsMount)
                {
                    Agent rider = victim.RiderAgent;
                    if (rider != null && rider.IsPlayerControlled)
                        isVictimPlayerControlled = true;
                }

                if (!isVictimPlayerControlled)
                    return true;

                Agent attacker = attackInformation.AttackerAgent;
                bool isAttackerPlayerControlled = false;
                if (attacker != null)
                {
                    isAttackerPlayerControlled = attacker.IsPlayerControlled;
                    if (attacker.IsMount)
                    {
                        Agent rider = attacker.RiderAgent;
                        if (rider != null && rider.IsPlayerControlled)
                            isAttackerPlayerControlled = true;
                    }
                }

                if (isAttackerPlayerControlled)
                    return true;

                if (victim.IsHuman && settings.GodModePlayer)
                {
                    __result = 0f;
                    return false;
                }

                if (victim.IsMount && settings.GodModeMount)
                {
                    __result = 0f;
                    return false;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration", "godmode_errors.log");
                    File.AppendAllText(logPath, $"{DateTime.Now}: CalculateDamage patch error: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Hero), "get_Age")]
    public static class Patch_HeroAge
    {
        static bool Prefix(Hero __instance, ref float __result)
        {
            try
            {
                if (__instance == null || Hero.MainHero == null || __instance != Hero.MainHero)
                    return true;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return true;

                if (settings.DisableAging)
                {
                    __result = settings.PlayerAge;
                    return false;
                }
            }
            catch { }

            return true;
        }
    }

    // =========================================================================
    // 2. ОСНОВНОЙ МОДУЛЬ
    // =========================================================================

    public class MySubModule : MBSubModuleBase
    {
        public static MySubModule Current { get; private set; }

        private float _lastPlayerDamageTime = -100f;
        private float _playerRegenAccumulator = 0f;
        private float _lastMountDamageTime = -100f;
        private float _mountRegenAccumulator = 0f;
        private Agent _currentMount = null;
        private float _lastAppliedAge = -1f;

        private Dictionary<Hero, int> _killsInCurrentMission = new Dictionary<Hero, int>();
        private bool _isMissionValidForKillCounting = false;
        private bool _isGameFullyLoaded = false;

        private const string SaveFileName = "JustRegenerationData.txt";

        private string SaveFilePath
        {
            get
            {
                string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration");
                if (!Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);
                return Path.Combine(configDir, SaveFileName);
            }
        }

        private string ErrorLogPath => Path.Combine(Path.GetDirectoryName(SaveFilePath), "JustRegeneration_errors.log");

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Current = this;

            var harmony = new Harmony("com.yourname.justregeneration");
            harmony.PatchAll();

            TextObject initMsg = new TextObject("{=JR_InitMessage}Just Regeneration: Initialized.");
            if (Campaign.Current != null)
                InformationManager.DisplayMessage(new InformationMessage(initMsg.ToString()));
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);

            try
            {
                if (!(game.GameType is Campaign))
                    return;

                _isGameFullyLoaded = true;
                LoadData();
                CleanupSaveFile();

                CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, (CampaignGameStarter starter) =>
                {
                    _isGameFullyLoaded = true;
                    SyncAgeOnLoad();
                    var settings = JustRegenerationSettings.Instance;
                    settings?.UpdatePlayerDisplayKills();
                    CleanupSaveFile();
                    settings?.RefreshHeroList();
                });

                CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, () =>
                {
                    try
                    {
                        var settings = JustRegenerationSettings.Instance;
                        if (settings != null && settings.EnableMod && settings.DisableAging && _isGameFullyLoaded && Hero.MainHero != null)
                            ApplyAgeToHero(Hero.MainHero, settings.PlayerAge);
                    }
                    catch (Exception ex) { LogError("DailyTickEvent", ex); }
                });

                CampaignEvents.OnBeforePlayerCharacterChangedEvent.AddNonSerializedListener(this, OnBeforePlayerCharacterChanged);
                CampaignEvents.OnPlayerBattleEndEvent.AddNonSerializedListener(this, OnPlayerBattleEnd);
                CampaignEvents.OnPlayerCharacterChangedEvent.AddNonSerializedListener(this, OnPlayerCharacterChanged);
                CampaignEvents.OnMissionEndedEvent.AddNonSerializedListener(this, OnMissionEnded);
            }
            catch (Exception ex)
            {
                LogError("OnGameStart", ex);
                try
                {
                    TextObject errorMsg = new TextObject("{=JR_ErrorLoadingData}Just Regeneration: Error loading data: {MESSAGE}");
                    errorMsg.SetTextVariable("MESSAGE", ex.Message);
                    if (Campaign.Current != null)
                        InformationManager.DisplayMessage(new InformationMessage(errorMsg.ToString()));
                }
                catch { }
            }
        }

        public override void OnGameEnd(Game game)
        {
            SaveData();
            base.OnGameEnd(game);
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            try
            {
                if (mission.GetMissionBehavior<RegenerationMissionBehavior>() == null)
                    mission.AddMissionBehavior(new RegenerationMissionBehavior(this));

                _lastPlayerDamageTime = -100f;
                _lastMountDamageTime = -100f;
                _playerRegenAccumulator = 0f;
                _mountRegenAccumulator = 0f;
                _currentMount = null;
                _killsInCurrentMission.Clear();

                _isMissionValidForKillCounting = mission.CombatType != Mission.MissionCombatType.ArenaCombat
                                                 && mission.CombatType != Mission.MissionCombatType.NoCombat
                                                 && mission.Mode != MissionMode.Duel
                                                 && mission.Mode != MissionMode.Tournament;
            }
            catch (Exception ex) { LogError("OnMissionBehaviorInitialize", ex); }
        }

        public void OnAgentRemovedInternal(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
        {
            try
            {
                if (agentState != AgentState.Killed)
                    return;

                if (!_isMissionValidForKillCounting)
                    return;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return;

                if (affectorAgent == null || !affectorAgent.IsPlayerControlled)
                    return;
                if (affectedAgent == null || !affectedAgent.IsHuman)
                    return;
                if (affectorAgent.Team == null || affectedAgent.Team == null)
                    return;
                if (!affectorAgent.Team.IsEnemyOf(affectedAgent.Team))
                    return;

                CharacterObject character = affectorAgent.Character as CharacterObject;
                if (character == null)
                    return;
                Hero killerHero = character.HeroObject;
                if (killerHero == null)
                    return;

                bool isPlayer = killerHero == Hero.MainHero;
                bool isFamily = !isPlayer && killerHero.Clan != null && killerHero.Clan == Hero.MainHero?.Clan && !killerHero.IsWanderer;
                bool isCompanion = killerHero.IsWanderer;

                if (!isPlayer && !isFamily && !isCompanion)
                    return;
                if (isPlayer && !settings.EnablePlayerRejuvenation)
                    return;
                if (isFamily && !settings.EnableFamilyRejuvenation)
                    return;
                if (isCompanion && !settings.EnableCompanionRejuvenation)
                    return;

                if (_killsInCurrentMission.ContainsKey(killerHero))
                    _killsInCurrentMission[killerHero]++;
                else
                    _killsInCurrentMission[killerHero] = 1;
            }
            catch (Exception ex) { LogError("OnAgentRemovedInternal", ex); }
        }

        private void OnMissionEnded(IMission mission)
        {
            try
            {
                if (mission == null || _killsInCurrentMission.Count == 0)
                    return;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return;

                foreach (var kvp in _killsInCurrentMission)
                {
                    Hero hero = kvp.Key;
                    int killsInMission = kvp.Value;
                    settings.AddKillsForHero(hero, killsInMission);
                }
                SaveData();

                foreach (var kvp in _killsInCurrentMission)
                {
                    Hero hero = kvp.Key;
                    if (hero == null) continue;

                    bool isPlayer = hero == Hero.MainHero;
                    bool isFamily = !isPlayer && hero.Clan != null && hero.Clan == Hero.MainHero?.Clan && !hero.IsWanderer;
                    bool isCompanion = hero.IsWanderer;

                    if (!isPlayer && !isFamily && !isCompanion)
                        continue;

                    bool enabled;
                    int minAge, killsNeeded, daysPer;
                    if (isPlayer)
                    {
                        enabled = settings.EnablePlayerRejuvenation;
                        minAge = settings.PlayerMinimumAge;
                        killsNeeded = settings.PlayerKillsPerRejuvenation;
                        daysPer = settings.PlayerDaysPerRejuvenation;
                    }
                    else if (isFamily)
                    {
                        enabled = settings.EnableFamilyRejuvenation;
                        minAge = settings.FamilyMinimumAge;
                        killsNeeded = settings.FamilyKillsPerRejuvenation;
                        daysPer = settings.FamilyDaysPerRejuvenation;
                    }
                    else // companion
                    {
                        enabled = settings.EnableCompanionRejuvenation;
                        minAge = settings.CompanionMinimumAge;
                        killsNeeded = settings.CompanionKillsPerRejuvenation;
                        daysPer = settings.CompanionDaysPerRejuvenation;
                    }

                    if (!enabled)
                        continue;

                    int totalKills = settings.GetKillsForHero(hero);
                    if (totalKills >= killsNeeded)
                    {
                        int times = totalKills / killsNeeded;
                        if (times > 0)
                        {
                            float currentAge = hero.Age;
                            float newAge = currentAge - (times * daysPer) / 365f;
                            float minAgeFloat = Math.Max(0f, minAge);
                            if (newAge < minAgeFloat)
                                newAge = minAgeFloat;

                            if (Math.Abs(newAge - currentAge) > 0.001f)
                            {
                                ApplyAgeToHero(hero, newAge);
                                int usedKills = times * killsNeeded;
                                int remaining = totalKills - usedKills;
                                settings.SetKillsForHero(hero, remaining);

                                if (isPlayer && Campaign.Current != null)
                                {
                                    try
                                    {
                                        TextObject ageMsg = new TextObject("{=JR_AgeReduced}Just Regeneration: {HERO} age reduced to {AGE:F1} years (used {USED} kills). Remaining kills: {REMAIN}.");
                                        ageMsg.SetTextVariable("HERO", hero.Name);
                                        ageMsg.SetTextVariable("AGE", newAge);
                                        ageMsg.SetTextVariable("USED", usedKills);
                                        ageMsg.SetTextVariable("REMAIN", remaining);
                                        InformationManager.DisplayMessage(new InformationMessage(ageMsg.ToString()));
                                    }
                                    catch { }
                                }
                                SaveData();
                            }
                        }
                    }
                    else
                    {
                        if (isPlayer && Campaign.Current != null)
                        {
                            int killsLeft = killsNeeded - (totalKills % killsNeeded);
                            if (killsLeft == 0) killsLeft = killsNeeded;
                            try
                            {
                                TextObject progressMsg = new TextObject("{=JR_KillsAccumulated}Just Regeneration: {HERO} accumulated {COUNT} kills. {NEEDED} more kills needed for next rejuvenation.");
                                progressMsg.SetTextVariable("HERO", hero.Name);
                                progressMsg.SetTextVariable("COUNT", totalKills);
                                progressMsg.SetTextVariable("NEEDED", killsLeft);
                                InformationManager.DisplayMessage(new InformationMessage(progressMsg.ToString()));
                            }
                            catch { }
                        }
                    }
                }

                _killsInCurrentMission.Clear();
            }
            catch (Exception ex) { LogError("OnMissionEnded", ex); }
        }

        private void OnPlayerBattleEnd(MapEvent mapEvent) { }

        private void ApplyAgeToHero(Hero hero, float targetAge)
        {
            try
            {
                if (hero == null || !_isGameFullyLoaded || Campaign.Current == null || !Campaign.Current.GameStarted)
                    return;

                targetAge = Math.Max(0f, Math.Min(100f, targetAge));

                var field = typeof(Hero).GetField("_defaultAge", BindingFlags.NonPublic | BindingFlags.Instance);
                var birthDayField = typeof(Hero).GetField("_birthDay", BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                    field.SetValue(hero, targetAge);

                if (birthDayField != null)
                {
                    int fullYears = (int)Math.Floor(targetAge);
                    CampaignTime now;
                    try { now = CampaignTime.Now; } catch { now = CampaignTime.Zero; }
                    CampaignTime newBirth = now - CampaignTime.Years(fullYears);
                    birthDayField.SetValue(hero, newBirth);
                }

                if (hero == Hero.MainHero)
                {
                    _lastAppliedAge = targetAge;
                    var settings = JustRegenerationSettings.Instance;
                    settings?.SyncAgeFromHero(targetAge);
                }
            }
            catch (Exception ex) { LogError("ApplyAgeToHero", ex); }
        }

        public void ApplyPlayerAge()
        {
            var settings = JustRegenerationSettings.Instance;
            if (settings != null && Hero.MainHero != null)
                ApplyAgeToHero(Hero.MainHero, settings.PlayerAge);
        }

        public void LockCurrentPlayerAge()
        {
            try
            {
                if (!_isGameFullyLoaded || Hero.MainHero == null) return;
                float currentAge = Hero.MainHero.Age;
                var settings = JustRegenerationSettings.Instance;
                settings?.SyncAgeFromHero(currentAge);
            }
            catch (Exception ex)
            {
                LogError("LockCurrentPlayerAge", ex);
                try
                {
                    TextObject errorMsg = new TextObject("{=JR_ErrorLockingAge}Just Regeneration: Error locking age: {MESSAGE}");
                    errorMsg.SetTextVariable("MESSAGE", ex.Message);
                    if (Campaign.Current != null)
                        InformationManager.DisplayMessage(new InformationMessage(errorMsg.ToString()));
                }
                catch { }
            }
        }

        public void ResetAccumulatedKills()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                if (Campaign.Current == null || Hero.MainHero == null)
                    return;

                settings.SetKillsForHero(Hero.MainHero, 0);
                SaveData();

                TextObject resetMsg = new TextObject("{=JR_ResetKillsDone}Just Regeneration: Kill counter for main hero reset to 0.");
                if (Campaign.Current != null)
                    InformationManager.DisplayMessage(new InformationMessage(resetMsg.ToString()));
            }
            catch (Exception ex)
            {
                LogError("ResetAccumulatedKills", ex);
                try
                {
                    if (Campaign.Current != null)
                    {
                        TextObject errorMsg = new TextObject("{=JR_ErrorReset}Error resetting kills: {MESSAGE}");
                        errorMsg.SetTextVariable("MESSAGE", ex.Message);
                        InformationManager.DisplayMessage(new InformationMessage(errorMsg.ToString()));
                    }
                }
                catch { }
            }
        }

        private void SyncAgeOnLoad()
        {
            try
            {
                if (!_isGameFullyLoaded || Hero.MainHero == null) return;
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                if (settings.DisableAging)
                {
                    float currentAge = Hero.MainHero.Age;
                    settings.SyncAgeFromHero(currentAge);
                    ApplyAgeToHero(Hero.MainHero, settings.PlayerAge);
                }
            }
            catch (Exception ex) { LogError("SyncAgeOnLoad", ex); }
        }

        public void RegisterDamage(Agent victim, float damage)
        {
            try
            {
                if (victim == null) return;
                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod || damage <= 0) return;
                var mission = Mission.Current;
                if (mission == null) return;

                if (victim.IsHuman && victim.IsPlayerControlled && settings.EnablePlayerRegen && victim.Health > 0)
                {
                    _lastPlayerDamageTime = mission.CurrentTime;
                    _playerRegenAccumulator = 0f;
                }
                else if (victim.IsMount)
                {
                    Agent rider = victim.RiderAgent;
                    if (rider != null && rider.IsPlayerControlled && settings.EnableMountRegen && victim.Health > 0)
                    {
                        _lastMountDamageTime = mission.CurrentTime;
                        _mountRegenAccumulator = 0f;
                        _currentMount = victim;
                    }
                }
            }
            catch (Exception ex) { LogError("RegisterDamage", ex); }
        }

        public void UpdateRegeneration(float dt)
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod) return;

                Agent playerAgent = Agent.Main;
                if (playerAgent == null || playerAgent.Health <= 0) return;

                var mission = Mission.Current;
                if (mission == null) return;

                float currentTime = mission.CurrentTime;

                if (settings.EnablePlayerRegen && !settings.GodModePlayer)
                {
                    float timeSinceLastDamage = currentTime - _lastPlayerDamageTime;
                    if (timeSinceLastDamage >= settings.PlayerDelay)
                    {
                        _playerRegenAccumulator += dt;
                        while (_playerRegenAccumulator >= 1f)
                        {
                            _playerRegenAccumulator -= 1f;
                            float newHealth = Math.Min(playerAgent.Health + settings.PlayerRate, playerAgent.HealthLimit);
                            if (newHealth > playerAgent.Health)
                                playerAgent.Health = newHealth;
                            if (playerAgent.Health >= playerAgent.HealthLimit)
                                break;
                        }
                    }
                    else _playerRegenAccumulator = 0f;
                }

                if (playerAgent.HasMount && settings.EnableMountRegen && !settings.GodModeMount)
                {
                    Agent mount = playerAgent.MountAgent;
                    if (mount != null && mount.Health > 0)
                    {
                        if (mount != _currentMount)
                        {
                            _lastMountDamageTime = -100f;
                            _mountRegenAccumulator = 0f;
                            _currentMount = mount;
                        }

                        float timeSinceLastDamage = currentTime - _lastMountDamageTime;
                        if (timeSinceLastDamage >= settings.MountDelay)
                        {
                            _mountRegenAccumulator += dt;
                            while (_mountRegenAccumulator >= 1f)
                            {
                                _mountRegenAccumulator -= 1f;
                                float newHealth = Math.Min(mount.Health + settings.MountRate, mount.HealthLimit);
                                if (newHealth > mount.Health)
                                    mount.Health = newHealth;
                                if (mount.Health >= mount.HealthLimit)
                                    break;
                            }
                        }
                        else _mountRegenAccumulator = 0f;
                    }
                    else
                    {
                        _currentMount = null;
                        _lastMountDamageTime = -100f;
                        _mountRegenAccumulator = 0f;
                    }
                }
                else
                {
                    _currentMount = null;
                    _lastMountDamageTime = -100f;
                    _mountRegenAccumulator = 0f;
                }
            }
            catch (Exception ex) { LogError("UpdateRegeneration", ex); }
        }

        // =====================================================================
        // МЕТОДЫ ДЛЯ РАБОТЫ СО СПИСКОМ КЛАНА И СБРОСОМ СЧЁТЧИКОВ
        // =====================================================================

        public List<string> GetCurrentClanMemberIds()
        {
            var result = new List<string>();
            if (!_isGameFullyLoaded || Campaign.Current == null || Hero.MainHero == null)
            {
                LogError("GetCurrentClanMemberIds", new Exception($"Game not fully loaded: _isGameFullyLoaded={_isGameFullyLoaded}, Campaign.Current={Campaign.Current != null}, Hero.MainHero={Hero.MainHero != null}"));
                return result;
            }

            var clan = Hero.MainHero.Clan;
            if (clan == null)
            {
                LogError("GetCurrentClanMemberIds", new Exception("Clan is null for MainHero"));
                return result;
            }

            foreach (var hero in clan.Heroes)
            {
                if (hero != null && hero.IsAlive)
                    result.Add(hero.StringId);
            }
            return result;
        }

        public void CleanupSaveFile()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var currentIds = GetCurrentClanMemberIds();
                if (currentIds.Count == 0) return;

                var toRemove = settings.AccumulatedKillsPerHero.Keys
                    .Where(id => !currentIds.Contains(id))
                    .ToList();

                foreach (var id in toRemove)
                    settings.AccumulatedKillsPerHero.Remove(id);

                if (toRemove.Count > 0)
                {
                    SaveData();
                }
            }
            catch (Exception ex) { LogError("CleanupSaveFile", ex); }
        }

        public int GetKillsForHeroId(string heroId)
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null || string.IsNullOrEmpty(heroId))
                    return 0;
                return settings.AccumulatedKillsPerHero.TryGetValue(heroId, out int kills) ? kills : 0;
            }
            catch { return 0; }
        }

        public void ResetKillsForHeroId(string heroId)
        {
            try
            {
                if (string.IsNullOrEmpty(heroId)) return;
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;
                if (settings.AccumulatedKillsPerHero.ContainsKey(heroId))
                    settings.AccumulatedKillsPerHero[heroId] = 0;
                else
                    settings.AccumulatedKillsPerHero[heroId] = 0;
                SaveData();
                if (Campaign.Current != null)
                {
                    TextObject msg = new TextObject("{=JR_ResetSingleDone}Just Regeneration: Kill counter reset for hero.");
                    InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                }
            }
            catch (Exception ex) { LogError("ResetKillsForHeroId", ex); }
        }

        public void ResetAllKills()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;
                var currentIds = GetCurrentClanMemberIds();
                foreach (var id in currentIds)
                {
                    settings.AccumulatedKillsPerHero[id] = 0;
                }
                SaveData();
                if (Campaign.Current != null)
                {
                    TextObject msg = new TextObject("{=JR_ResetAllDone}Just Regeneration: All clan member kill counters reset to 0.");
                    InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                }
            }
            catch (Exception ex) { LogError("ResetAllKills", ex); }
        }

        // =====================================================================

        private void SaveData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var lines = settings.AccumulatedKillsPerHero.Select(kvp => $"{kvp.Key}|{kvp.Value}").ToList();
                File.WriteAllLines(SaveFilePath, lines);
            }
            catch (Exception ex) { LogError("SaveData", ex); }
        }

        private void LoadData()
        {
            try
            {
                if (!File.Exists(SaveFilePath)) return;
                string[] lines = File.ReadAllLines(SaveFilePath);
                if (lines.Length == 0) return;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                if (!lines[0].Contains('|'))
                {
                    if (int.TryParse(lines[0], out int oldKills) && Hero.MainHero != null)
                        settings.SetKillsForHero(Hero.MainHero, oldKills);
                    return;
                }

                foreach (string line in lines)
                {
                    string[] parts = line.Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int kills))
                        settings.AccumulatedKillsPerHero[parts[0]] = kills;
                }
                settings.UpdatePlayerDisplayKills();
            }
            catch (Exception ex) { LogError("LoadData", ex); }
        }

        private void LogError(string context, Exception ex)
        {
            try
            {
                File.AppendAllText(ErrorLogPath,
                    $"{DateTime.Now}: [{context}] {ex.Message}\n{ex.StackTrace}\n\n");
            }
            catch { }
        }

        private void OnBeforePlayerCharacterChanged(Hero oldPlayer, Hero newPlayer) { }

        private void OnPlayerCharacterChanged(Hero oldPlayer, Hero newPlayer, MobileParty newMainParty, bool isMainPartyChanged)
        {
            // Обновляем список при смене игрока (например, после смерти/наследования)
            var settings = JustRegenerationSettings.Instance;
            settings?.RefreshHeroList();
        }
    }

    // =========================================================================
    // 4. ПОВЕДЕНИЕ МИССИИ
    // =========================================================================

    public class RegenerationMissionBehavior : MissionBehavior
    {
        private MySubModule _subModule;

        public RegenerationMissionBehavior(MySubModule subModule)
        {
            _subModule = subModule;
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            _subModule?.UpdateRegeneration(dt);
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, killingBlow);
            _subModule?.OnAgentRemovedInternal(affectedAgent, affectorAgent, agentState, killingBlow);
        }
    }
}