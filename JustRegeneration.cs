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

                CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, (CampaignGameStarter starter) =>
                {
                    _isGameFullyLoaded = true;
                    SyncAgeOnLoad();
                    var settings = JustRegenerationSettings.Instance;
                    settings?.UpdatePlayerDisplayKills();
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
        private void OnPlayerCharacterChanged(Hero oldPlayer, Hero newPlayer, MobileParty newMainParty, bool isMainPartyChanged) { }
    }

    // =========================================================================
    // 3. НАСТРОЙКИ (MCM)
    // =========================================================================

    public class JustRegenerationSettings : AttributeGlobalSettings<JustRegenerationSettings>
    {
        public override string Id => "JustRegenerationSettings";
        public override string DisplayName => "Just Regeneration";
        public override string FormatType => "json";

        // --- Основные настройки ---
        private bool _enableMod = false;
        [SettingPropertyGroup("{=JR_GroupMain}Main", GroupOrder = -1)]
        [SettingPropertyBool("{=JR_EnableMod}Enable mod", Order = 0, RequireRestart = false, HintText = "{=JR_EnableMod_Hint}Master switch for all features.")]
        public bool EnableMod
        {
            get => _enableMod;
            set { if (_enableMod != value) { _enableMod = value; OnPropertyChanged(nameof(EnableMod)); } }
        }

        // --- God Mode ---
        private bool _godModePlayer = false;
        [SettingPropertyGroup("{=JR_GroupGodMode}God Mode")]
        [SettingPropertyBool("{=JR_GodModePlayer}God Mode (Player)", Order = 10, RequireRestart = false, HintText = "{=JR_GodModePlayer_Hint}Player takes no damage.")]
        public bool GodModePlayer
        {
            get => _godModePlayer;
            set { if (_godModePlayer != value) { _godModePlayer = value; if (value) _enablePlayerRegen = false; OnPropertyChanged(nameof(EnablePlayerRegen)); OnPropertyChanged(nameof(GodModePlayer)); } }
        }

        private bool _godModeMount = false;
        [SettingPropertyGroup("{=JR_GroupGodMode}God Mode")]
        [SettingPropertyBool("{=JR_GodModeMount}God Mode (Mount)", Order = 11, RequireRestart = false, HintText = "{=JR_GodModeMount_Hint}Player's mount takes no damage.")]
        public bool GodModeMount
        {
            get => _godModeMount;
            set { if (_godModeMount != value) { _godModeMount = value; if (value) _enableMountRegen = false; OnPropertyChanged(nameof(EnableMountRegen)); OnPropertyChanged(nameof(GodModeMount)); } }
        }

        // --- Regeneration ---
        private bool _enablePlayerRegen = false;
        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyBool("{=JR_EnablePlayerRegen}Enable Player Regen", Order = 20, RequireRestart = false, HintText = "{=JR_EnablePlayerRegen_Hint}Enable health regeneration for player.")]
        public bool EnablePlayerRegen
        {
            get => _enablePlayerRegen;
            set { if (_enablePlayerRegen != value) { _enablePlayerRegen = value; if (value) _godModePlayer = false; OnPropertyChanged(nameof(GodModePlayer)); OnPropertyChanged(nameof(EnablePlayerRegen)); } }
        }

        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyFloatingInteger("{=JR_PlayerDelay}Player Delay (seconds)", 0f, 60f, Order = 21, RequireRestart = false, HintText = "{=JR_PlayerDelay_Hint}Seconds after last damage before regen starts.")]
        public float PlayerDelay { get; set; } = 10f;

        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyFloatingInteger("{=JR_PlayerRate}Player Rate (HP/sec)", 0f, 100f, Order = 22, RequireRestart = false, HintText = "{=JR_PlayerRate_Hint}Health restored per second.")]
        public float PlayerRate { get; set; } = 2f;

        private bool _enableMountRegen = false;
        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyBool("{=JR_EnableMountRegen}Enable Mount Regen", Order = 30, RequireRestart = false, HintText = "{=JR_EnableMountRegen_Hint}Enable health regeneration for player's mount.")]
        public bool EnableMountRegen
        {
            get => _enableMountRegen;
            set { if (_enableMountRegen != value) { _enableMountRegen = value; if (value) _godModeMount = false; OnPropertyChanged(nameof(GodModeMount)); OnPropertyChanged(nameof(EnableMountRegen)); } }
        }

        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyFloatingInteger("{=JR_MountDelay}Mount Delay (seconds)", 0f, 60f, Order = 31, RequireRestart = false, HintText = "{=JR_MountDelay_Hint}Seconds after last damage before regen starts.")]
        public float MountDelay { get; set; } = 10f;

        [SettingPropertyGroup("{=JR_GroupRegen}Regeneration")]
        [SettingPropertyFloatingInteger("{=JR_MountRate}Mount Rate (HP/sec)", 0f, 100f, Order = 32, RequireRestart = false, HintText = "{=JR_MountRate_Hint}Health restored per second.")]
        public float MountRate { get; set; } = 2f;

        // --- Age (упрощён) ---
        private bool _disableAging = false;
        [SettingPropertyGroup("{=JR_GroupAge}Age")]
        [SettingPropertyBool("{=JR_DisableAging}Disable aging (Player only)", Order = 40, RequireRestart = false, HintText = "{=JR_DisableAging_Hint}If enabled, the player's hero will not age. Current age will be locked.")]
        public bool DisableAging
        {
            get => _disableAging;
            set { if (_disableAging != value) { _disableAging = value; if (value && MySubModule.Current != null) { MySubModule.Current.LockCurrentPlayerAge(); } OnPropertyChanged(nameof(DisableAging)); } }
        }

        private float _playerAge = 25f;
        [SettingPropertyGroup("{=JR_GroupAge}Age")]
        [SettingPropertyFloatingInteger("{=JR_PlayerAge}Player Age", 0f, 100f, Order = 41, RequireRestart = false, HintText = "{=JR_PlayerAge_Hint}Set the exact age of the player hero (0-100).")]
        public float PlayerAge
        {
            get => _playerAge;
            set { if (Math.Abs(_playerAge - value) > 0.01f) { _playerAge = value; OnPropertyChanged(nameof(PlayerAge)); } }
        }

        private bool _applyAgeButton = false;
        [SettingPropertyGroup("{=JR_GroupAge}Age")]
        [SettingPropertyButton("{=JR_ApplyAgeButton}Apply Age Now", Order = 42, RequireRestart = false, HintText = "{=JR_ApplyAgeButton_Hint}Instantly sets the player's age to the value above (even if aging is not disabled).")]
        public bool ApplyAgeButton
        {
            get => _applyAgeButton;
            set
            {
                if (value)
                {
                    try
                    {
                        // FIX: Добавлена проверка состояния игры перед применением возраста
                        if (Campaign.Current != null && Hero.MainHero != null && MySubModule.Current != null)
                            MySubModule.Current.ApplyPlayerAge();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration", "mcm_errors.log");
                            File.AppendAllText(logPath, $"{DateTime.Now}: ApplyAgeButton error: {ex.Message}\n{ex.StackTrace}\n");
                        }
                        catch { }
                    }
                    _applyAgeButton = false;
                    OnPropertyChanged(nameof(ApplyAgeButton));
                }
            }
        }

        // --- Rejuvenation (Player) ---
        private bool _enablePlayerRejuvenation = false;
        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyBool("{=JR_EnablePlayerRejuvenation}Enable", Order = 50, RequireRestart = false, HintText = "{=JR_EnablePlayerRejuvenation_Hint}Enable age reduction for the player hero.")]
        public bool EnablePlayerRejuvenation
        {
            get => _enablePlayerRejuvenation;
            set { if (_enablePlayerRejuvenation != value) { _enablePlayerRejuvenation = value; OnPropertyChanged(nameof(EnablePlayerRejuvenation)); } }
        }

        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyInteger("{=JR_PlayerMinimumAge}Minimum age", 1, 100, Order = 51, RequireRestart = false, HintText = "{=JR_PlayerMinimumAge_Hint}Player cannot be younger than this age.")]
        public int PlayerMinimumAge { get; set; } = 18;

        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyInteger("{=JR_PlayerKillsPerRejuvenation}Kills needed per rejuvenation", 1, 1000000, Order = 52, RequireRestart = false, HintText = "{=JR_PlayerKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
        public int PlayerKillsPerRejuvenation { get; set; } = 365;

        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyInteger("{=JR_PlayerDaysPerRejuvenation}Days per rejuvenation", 1, 365, Order = 53, RequireRestart = false, HintText = "{=JR_PlayerDaysPerRejuvenation_Hint}How many days to rejuvenate when threshold is met.")]
        public int PlayerDaysPerRejuvenation { get; set; } = 365;

        private string _playerDisplayAccumulatedKills = "0";
        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyText("{=JR_PlayerDisplayKills}Accumulated kills (player)", Order = 54, RequireRestart = false, HintText = "{=JR_PlayerDisplayKills_Hint}Current number of kills accumulated for the player.")]
        public string PlayerDisplayAccumulatedKills
        {
            get => _playerDisplayAccumulatedKills;
            set { if (_playerDisplayAccumulatedKills != value) { _playerDisplayAccumulatedKills = value; OnPropertyChanged(nameof(PlayerDisplayAccumulatedKills)); } }
        }

        private bool _resetKills = false;
        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyButton("{=JR_ResetKillsButton}Reset kill counter (player only)", Order = 55, RequireRestart = false, HintText = "{=JR_ResetKillsButton_Hint}Reset accumulated kills for the player to 0.")]
        public bool ResetKillsButton
        {
            get => _resetKills;
            set
            {
                // FIX: Добавлена обработка исключений и проверка состояния игры
                if (value)
                {
                    try
                    {
                        if (Campaign.Current != null && Hero.MainHero != null && MySubModule.Current != null)
                        {
                            MySubModule.Current.ResetAccumulatedKills();
                        }
                        // Если кампания не загружена, просто игнорируем нажатие (ничего не делаем)
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration", "mcm_errors.log");
                            File.AppendAllText(logPath, $"{DateTime.Now}: ResetKillsButton error: {ex.Message}\n{ex.StackTrace}\n");
                        }
                        catch { }
                    }
                    _resetKills = false;
                    OnPropertyChanged(nameof(ResetKillsButton));
                }
            }
        }

        // --- Rejuvenation (Family) ---
        private bool _enableFamilyRejuvenation = false;
        [SettingPropertyGroup("{=JR_GroupRejuvenationFamily}Rejuvenation (Family Members)")]
        [SettingPropertyBool("{=JR_EnableFamilyRejuvenation}Enable", Order = 60, RequireRestart = false, HintText = "{=JR_EnableFamilyRejuvenation_Hint}Enable age reduction for family members.")]
        public bool EnableFamilyRejuvenation
        {
            get => _enableFamilyRejuvenation;
            set { if (_enableFamilyRejuvenation != value) { _enableFamilyRejuvenation = value; OnPropertyChanged(nameof(EnableFamilyRejuvenation)); } }
        }

        [SettingPropertyGroup("{=JR_GroupRejuvenationFamily}Rejuvenation (Family Members)")]
        [SettingPropertyInteger("{=JR_FamilyMinimumAge}Minimum age", 1, 100, Order = 61, RequireRestart = false, HintText = "{=JR_FamilyMinimumAge_Hint}Family members cannot be younger than this age.")]
        public int FamilyMinimumAge { get; set; } = 18;

        [SettingPropertyGroup("{=JR_GroupRejuvenationFamily}Rejuvenation (Family Members)")]
        [SettingPropertyInteger("{=JR_FamilyKillsPerRejuvenation}Kills needed per rejuvenation", 1, 1000000, Order = 62, RequireRestart = false, HintText = "{=JR_FamilyKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
        public int FamilyKillsPerRejuvenation { get; set; } = 365;

        [SettingPropertyGroup("{=JR_GroupRejuvenationFamily}Rejuvenation (Family Members)")]
        [SettingPropertyInteger("{=JR_FamilyDaysPerRejuvenation}Days per rejuvenation", 1, 365, Order = 63, RequireRestart = false, HintText = "{=JR_FamilyDaysPerRejuvenation_Hint}How many days to rejuvenate when threshold is met.")]
        public int FamilyDaysPerRejuvenation { get; set; } = 365;

        // --- Rejuvenation (Companions) ---
        private bool _enableCompanionRejuvenation = false;
        [SettingPropertyGroup("{=JR_GroupRejuvenationCompanions}Rejuvenation (Companions)")]
        [SettingPropertyBool("{=JR_EnableCompanionRejuvenation}Enable", Order = 70, RequireRestart = false, HintText = "{=JR_EnableCompanionRejuvenation_Hint}Enable age reduction for companions.")]
        public bool EnableCompanionRejuvenation
        {
            get => _enableCompanionRejuvenation;
            set { if (_enableCompanionRejuvenation != value) { _enableCompanionRejuvenation = value; OnPropertyChanged(nameof(EnableCompanionRejuvenation)); } }
        }

        [SettingPropertyGroup("{=JR_GroupRejuvenationCompanions}Rejuvenation (Companions)")]
        [SettingPropertyInteger("{=JR_CompanionMinimumAge}Minimum age", 1, 100, Order = 71, RequireRestart = false, HintText = "{=JR_CompanionMinimumAge_Hint}Companions cannot be younger than this age.")]
        public int CompanionMinimumAge { get; set; } = 18;

        [SettingPropertyGroup("{=JR_GroupRejuvenationCompanions}Rejuvenation (Companions)")]
        [SettingPropertyInteger("{=JR_CompanionKillsPerRejuvenation}Kills needed per rejuvenation", 1, 1000000, Order = 72, RequireRestart = false, HintText = "{=JR_CompanionKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
        public int CompanionKillsPerRejuvenation { get; set; } = 365;

        [SettingPropertyGroup("{=JR_GroupRejuvenationCompanions}Rejuvenation (Companions)")]
        [SettingPropertyInteger("{=JR_CompanionDaysPerRejuvenation}Days per rejuvenation", 1, 365, Order = 73, RequireRestart = false, HintText = "{=JR_CompanionDaysPerRejuvenation_Hint}How many days to rejuvenate when threshold is met.")]
        public int CompanionDaysPerRejuvenation { get; set; } = 365;

        // --- Словарь убийств ---
        public Dictionary<string, int> AccumulatedKillsPerHero { get; set; } = new Dictionary<string, int>();

        public int GetKillsForHero(Hero hero)
        {
            if (hero == null) return 0;
            string id = hero.StringId;
            return AccumulatedKillsPerHero.TryGetValue(id, out int kills) ? kills : 0;
        }

        public void SetKillsForHero(Hero hero, int kills)
        {
            if (hero == null) return;
            string id = hero.StringId;
            AccumulatedKillsPerHero[id] = kills;
            if (hero == Hero.MainHero)
                UpdatePlayerDisplayKills();
        }

        public void AddKillsForHero(Hero hero, int killsToAdd)
        {
            if (hero == null || killsToAdd <= 0) return;
            int current = GetKillsForHero(hero);
            SetKillsForHero(hero, current + killsToAdd);
        }

        public void UpdatePlayerDisplayKills()
        {
            PlayerDisplayAccumulatedKills = (Hero.MainHero != null) ? GetKillsForHero(Hero.MainHero).ToString() : "0";
        }

        public void SyncAgeFromHero(float currentAge)
        {
            if (Math.Abs(_playerAge - currentAge) > 0.01f)
            {
                _playerAge = currentAge;
                OnPropertyChanged(nameof(PlayerAge));
            }
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