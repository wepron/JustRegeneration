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
        private static readonly FieldInfo _defaultAgeField =
            typeof(Hero).GetField("_defaultAge",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix(Hero __instance, ref float __result)
        {
            try
            {
                if (__instance == null)
                    return true;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return true;

                if (settings.DisableAgingFlags == null)
                    return true;

                string heroId = __instance.StringId;
                if (string.IsNullOrEmpty(heroId))
                    return true;

                if (settings.DisableAgingFlags.TryGetValue(heroId, out bool disabled) && disabled)
                {
                    if (settings.CustomAges != null &&
                        settings.CustomAges.TryGetValue(heroId, out float fixedAge))
                    {
                        __result = fixedAge;
                        return false;
                    }

                    if (_defaultAgeField != null)
                    {
                        float currentAge = (float)_defaultAgeField.GetValue(__instance);
                        if (settings.CustomAges != null)
                            settings.CustomAges[heroId] = currentAge;
                        __result = currentAge;
                        return false;
                    }
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
        private const string AgeSaveFileName = "JustRegenerationAgeData.txt";
        private const string PointsSaveFileName = "JustRegenerationPointsData.txt";

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

        private string AgeSaveFilePath => Path.Combine(Path.GetDirectoryName(SaveFilePath), AgeSaveFileName);
        private string PointsSaveFilePath => Path.Combine(Path.GetDirectoryName(SaveFilePath), PointsSaveFileName);
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

                try { LoadData(); } catch (Exception ex) { LogError("LoadData", ex); }
                try { LoadAgeData(); } catch (Exception ex) { LogError("LoadAgeData", ex); }
                try { LoadPointsData(); } catch (Exception ex) { LogError("LoadPointsData", ex); }
                try { CleanupSaveFile(); } catch (Exception ex) { LogError("CleanupSaveFile", ex); }
                try { CleanupAgeData(); } catch (Exception ex) { LogError("CleanupAgeData", ex); }
                try { CleanupPointsData(); } catch (Exception ex) { LogError("CleanupPointsData", ex); }

                var settings = JustRegenerationSettings.Instance;
                try { settings?.RefreshHeroList(); } catch (Exception ex) { LogError("RefreshHeroList", ex); }

                CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, (CampaignGameStarter starter) =>
                {
                    _isGameFullyLoaded = true;
                    SyncAgeOnLoad();
                    settings?.UpdatePlayerDisplayKills();
                    try { CleanupSaveFile(); } catch (Exception ex) { LogError("CleanupSaveFile (loaded)", ex); }
                    try { CleanupAgeData(); } catch (Exception ex) { LogError("CleanupAgeData (loaded)", ex); }
                    try { CleanupPointsData(); } catch (Exception ex) { LogError("CleanupPointsData (loaded)", ex); }
                    try { settings?.RefreshHeroList(); } catch (Exception ex) { LogError("RefreshHeroList (loaded)", ex); }
                });

                CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, () =>
                {
                    try
                    {
                        if (settings != null && settings.EnableMod && _isGameFullyLoaded)
                        {
                            // nothing to do here, aging is handled by patch
                        }
                    }
                    catch (Exception ex) { LogError("DailyTickEvent", ex); }
                });

                // Обновление списка при найме компаньона
                CampaignEvents.NewCompanionAdded.AddNonSerializedListener(this, (Hero companion) =>
                {
                    try
                    {
                        Hero mainHero;
                        try { mainHero = Hero.MainHero; }
                        catch { return; }

                        if (companion != null && mainHero != null && companion.Clan == mainHero.Clan)
                            settings?.RefreshHeroList();
                    }
                    catch (Exception ex) { LogError("NewCompanionAdded", ex); }
                });

                // Обновление списка при смене клана героя
                CampaignEvents.OnHeroChangedClanEvent.AddNonSerializedListener(this, (Hero hero, Clan oldClan) =>
                {
                    try
                    {
                        Hero mainHero;
                        try { mainHero = Hero.MainHero; }
                        catch { return; }

                        if (hero != null && mainHero != null && hero.Clan == mainHero.Clan)
                            settings?.RefreshHeroList();
                    }
                    catch (Exception ex) { LogError("OnHeroChangedClanEvent", ex); }
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
            SaveAgeData();
            SavePointsData();
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
                // Считаем поверженных: и убитых (Killed), и вырубленных (Unconscious).
                // Килы засчитываются только героям клана игрока (ГГ + семья + компаньоны),
                // которые физически участвуют в текущей миссии. Если бой идёт без игрока
                // (авторасчёт на карте) — здесь вообще ничего не сработает, потому что
                // OnAgentRemoved вызывается только в миссиях.
                if (agentState != AgentState.Killed && agentState != AgentState.Unconscious)
                    return;

                if (!_isMissionValidForKillCounting)
                    return;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                    return;

                if (affectorAgent == null || affectedAgent == null)
                    return;

                // Если ударил маунт (затоптал), победу засчитываем его всаднику.
                Agent actualAffector = affectorAgent;
                if (affectorAgent.IsMount)
                {
                    Agent rider = affectorAgent.RiderAgent;
                    if (rider != null)
                        actualAffector = rider;
                    else
                        return; // вольный конь без всадника — никому не засчитываем
                }

                if (!actualAffector.IsHuman || !affectedAgent.IsHuman)
                    return;
                if (actualAffector.Team == null || affectedAgent.Team == null)
                    return;
                if (!actualAffector.Team.IsEnemyOf(affectedAgent.Team))
                    return;

                CharacterObject character = actualAffector.Character as CharacterObject;
                if (character == null)
                    return;
                Hero killerHero = character.HeroObject;
                if (killerHero == null)
                    return;

                Hero mainHero;
                try { mainHero = Hero.MainHero; }
                catch { return; }
                if (mainHero == null)
                    return;

                bool isPlayer = killerHero == mainHero;
                bool isFamily = !isPlayer && killerHero.Clan != null && killerHero.Clan == mainHero.Clan && !killerHero.IsWanderer;
                bool isCompanion = !isPlayer && killerHero.IsWanderer && killerHero.Clan == mainHero.Clan;

                if (!isPlayer && !isFamily && !isCompanion)
                    return;

                bool anyEnabled;
                if (isPlayer)
                    anyEnabled = settings.EnablePlayerRejuvenation
                                 || settings.EnableFocusPoints
                                 || settings.EnableAttentionPoints;
                else if (isFamily)
                    anyEnabled = settings.EnableFamilyRejuvenation
                                 || settings.EnableFamilyFocusPoints
                                 || settings.EnableFamilyAttentionPoints;
                else
                    anyEnabled = settings.EnableCompanionRejuvenation
                                 || settings.EnableCompanionFocusPoints
                                 || settings.EnableCompanionAttentionPoints;

                if (!anyEnabled)
                    return;

                if (_killsInCurrentMission.ContainsKey(killerHero))
                    _killsInCurrentMission[killerHero]++;
                else
                    _killsInCurrentMission[killerHero] = 1;
            }
            catch (Exception ex) { LogError("OnAgentRemovedInternal", ex); }
        }

        private void ProcessHeroAwards(Hero hero, bool isPlayer, bool isFamily, bool isCompanion, JustRegenerationSettings settings)
        {
            try
            {
                if (hero == null) return;
                // Раненых не отсекаем — очки должны начисляться и им.
                if (hero.HeroState == Hero.CharacterStates.Dead) return;
                if (hero.HeroDeveloper == null) return;

                // --- Focus Points ---
                bool focusEnabled;
                int focusThreshold;
                if (isPlayer) { focusEnabled = settings.EnableFocusPoints; focusThreshold = settings.KillsPerFocusPoint; }
                else if (isFamily) { focusEnabled = settings.EnableFamilyFocusPoints; focusThreshold = settings.FamilyKillsPerFocusPoint; }
                else { focusEnabled = settings.EnableCompanionFocusPoints; focusThreshold = settings.CompanionKillsPerFocusPoint; }

                if (focusEnabled && focusThreshold > 0)
                {
                    int totalFocusKills = settings.GetKillsForFocus(hero);
                    int pointsToAward = totalFocusKills / focusThreshold;
                    if (pointsToAward > 0)
                    {
                        hero.HeroDeveloper.UnspentFocusPoints += pointsToAward;
                        settings.SetKillsForFocus(hero, totalFocusKills - pointsToAward * focusThreshold);

                        try
                        {
                            TextObject msg = new TextObject("{=JR_FocusPointGained}Just Regeneration: {HERO} gained {POINTS} FOCUS point(s). Remaining kills: {REMAIN}.");
                            msg.SetTextVariable("HERO", hero.Name);
                            msg.SetTextVariable("POINTS", pointsToAward);
                            msg.SetTextVariable("REMAIN", totalFocusKills - pointsToAward * focusThreshold);
                            InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                        }
                        catch { }
                    }
                }

                // --- Attention Points ---
                bool attEnabled;
                int attThreshold;
                if (isPlayer) { attEnabled = settings.EnableAttentionPoints; attThreshold = settings.KillsPerAttentionPoint; }
                else if (isFamily) { attEnabled = settings.EnableFamilyAttentionPoints; attThreshold = settings.FamilyKillsPerAttentionPoint; }
                else { attEnabled = settings.EnableCompanionAttentionPoints; attThreshold = settings.CompanionKillsPerAttentionPoint; }

                if (attEnabled && attThreshold > 0)
                {
                    int totalAttKills = settings.GetKillsForAttention(hero);
                    int pointsToAward = totalAttKills / attThreshold;
                    if (pointsToAward > 0)
                    {
                        hero.HeroDeveloper.UnspentAttributePoints += pointsToAward;
                        settings.SetKillsForAttention(hero, totalAttKills - pointsToAward * attThreshold);

                        try
                        {
                            TextObject msg = new TextObject("{=JR_AttentionPointGained}Just Regeneration: {HERO} gained {POINTS} ATTRIBUTE point(s). Remaining kills: {REMAIN}.");
                            msg.SetTextVariable("HERO", hero.Name);
                            msg.SetTextVariable("POINTS", pointsToAward);
                            msg.SetTextVariable("REMAIN", totalAttKills - pointsToAward * attThreshold);
                            InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                        }
                        catch { }
                    }
                }

                // --- Rejuvenation ---
                ProcessRejuvenationForHero(hero, isPlayer, isFamily, isCompanion, settings);
            }
            catch (Exception ex) { LogError("ProcessHeroAwards", ex); }
        }

        private void ProcessRejuvenationForHero(Hero hero, bool isPlayer, bool isFamily, bool isCompanion, JustRegenerationSettings settings)
        {
            try
            {
                if (hero == null) return;
                // Раненых не отсекаем — омоложение должно работать и для них.
                if (hero.HeroState == Hero.CharacterStates.Dead) return;

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
                else
                {
                    enabled = settings.EnableCompanionRejuvenation;
                    minAge = settings.CompanionMinimumAge;
                    killsNeeded = settings.CompanionKillsPerRejuvenation;
                    daysPer = settings.CompanionDaysPerRejuvenation;
                }

                if (!enabled || killsNeeded <= 0) return;

                int totalKills = settings.GetKillsForHero(hero);
                if (totalKills < killsNeeded) return;

                int times = totalKills / killsNeeded;
                if (times <= 0) return;

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
            }
            catch (Exception ex) { LogError("ProcessRejuvenationForHero", ex); }
        }

        private void OnMissionEnded(IMission mission)
        {
            try
            {
                if (mission == null || _killsInCurrentMission.Count == 0)
                    return;

                var settings = JustRegenerationSettings.Instance;
                if (settings == null || !settings.EnableMod)
                {
                    _killsInCurrentMission.Clear();
                    return;
                }

                Hero mainHero;
                try { mainHero = Hero.MainHero; }
                catch { mainHero = null; }

                // 1) Начисляем килы всем героям клана, которые отличились в миссии.
                //    Раненых и активных считаем; пропускаем только реально мёртвых.
                foreach (var kvp in _killsInCurrentMission)
                {
                    if (kvp.Key == null) continue;
                    if (kvp.Key.HeroState == Hero.CharacterStates.Dead) continue;
                    settings.AddKillsForHero(kvp.Key, kvp.Value);
                }
                SaveData();

                // 2) Теперь для каждого героя прогоняем фокус/внимание/омоложение.
                foreach (var kvp in _killsInCurrentMission)
                {
                    Hero hero = kvp.Key;
                    if (hero == null) continue;
                    if (hero.HeroState == Hero.CharacterStates.Dead) continue;

                    bool isPlayer = mainHero != null && hero == mainHero;
                    bool isCompanion = !isPlayer && hero.IsWanderer;
                    bool isFamily = !isPlayer && !isCompanion;

                    if (isPlayer)
                    {
                        if (settings.EnableFocusPoints)
                            settings.AddKillsForFocus(hero, kvp.Value);
                        if (settings.EnableAttentionPoints)
                            settings.AddKillsForAttention(hero, kvp.Value);
                    }
                    else if (isFamily)
                    {
                        if (settings.EnableFamilyFocusPoints)
                            settings.AddKillsForFocus(hero, kvp.Value);
                        if (settings.EnableFamilyAttentionPoints)
                            settings.AddKillsForAttention(hero, kvp.Value);
                    }
                    else // companion
                    {
                        if (settings.EnableCompanionFocusPoints)
                            settings.AddKillsForFocus(hero, kvp.Value);
                        if (settings.EnableCompanionAttentionPoints)
                            settings.AddKillsForAttention(hero, kvp.Value);
                    }

                    ProcessHeroAwards(hero, isPlayer, isFamily, isCompanion, settings);
                }

                SavePointsData();
                SaveData();

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

                Hero mainHero;
                try { mainHero = Hero.MainHero; }
                catch { mainHero = null; }

                if (mainHero != null && hero == mainHero)
                {
                    _lastAppliedAge = targetAge;
                    var settings = JustRegenerationSettings.Instance;
                    settings?.SyncAgeFromHero(targetAge);
                }
            }
            catch (Exception ex) { LogError("ApplyAgeToHero", ex); }
        }

        public void ApplyPlayerAge() { }

        public void LockCurrentPlayerAge() { }

        public void ResetAccumulatedKills()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                if (Campaign.Current == null) return;

                Hero mainHero;
                try { mainHero = Hero.MainHero; }
                catch { return; }
                if (mainHero == null) return;

                settings.SetKillsForHero(mainHero, 0);
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

        private void SyncAgeOnLoad() { }

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
        // МЕТОДЫ ДЛЯ РАБОТЫ СО СПИСКОМ КЛАНА, УБИЙСТВАМИ, ВОЗРАСТОМ И СТАРЕНИЕМ
        // =====================================================================

        public List<string> GetCurrentClanMemberIds()
        {
            var result = new List<string>();

            if (!_isGameFullyLoaded || Campaign.Current == null)
                return result;

            Hero mainHero;
            try
            {
                mainHero = Hero.MainHero;
            }
            catch
            {
                return result;
            }

            if (mainHero == null)
                return result;

            var clan = mainHero.Clan;
            if (clan == null)
                return result;

            foreach (var hero in clan.Heroes)
            {
                // Раненых не отсекаем — только реально мёртвых.
                if (hero != null && hero.HeroState != Hero.CharacterStates.Dead)
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
                if (currentIds == null || currentIds.Count == 0) return;

                var toRemove = settings.AccumulatedKillsPerHero.Keys
                    .Where(id => !currentIds.Contains(id))
                    .ToList();

                foreach (var id in toRemove)
                    settings.AccumulatedKillsPerHero.Remove(id);

                if (toRemove.Count > 0)
                    SaveData();
            }
            catch (Exception ex) { LogError("CleanupSaveFile", ex); }
        }

        public void CleanupAgeData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var currentIds = GetCurrentClanMemberIds();
                if (currentIds == null || currentIds.Count == 0) return;

                var toRemoveAge = settings.CustomAges.Keys.Where(id => !currentIds.Contains(id)).ToList();
                foreach (var id in toRemoveAge)
                    settings.CustomAges.Remove(id);

                var toRemoveAging = settings.DisableAgingFlags.Keys.Where(id => !currentIds.Contains(id)).ToList();
                foreach (var id in toRemoveAging)
                    settings.DisableAgingFlags.Remove(id);

                if (toRemoveAge.Count > 0 || toRemoveAging.Count > 0)
                    SaveAgeData();
            }
            catch (Exception ex) { LogError("CleanupAgeData", ex); }
        }

        public void CleanupPointsData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var currentIds = GetCurrentClanMemberIds();
                if (currentIds == null || currentIds.Count == 0) return;

                var toRemoveFocus = settings.AccumulatedKillsForFocus.Keys.Where(id => !currentIds.Contains(id)).ToList();
                foreach (var id in toRemoveFocus)
                    settings.AccumulatedKillsForFocus.Remove(id);

                var toRemoveAttention = settings.AccumulatedKillsForAttention.Keys.Where(id => !currentIds.Contains(id)).ToList();
                foreach (var id in toRemoveAttention)
                    settings.AccumulatedKillsForAttention.Remove(id);

                if (toRemoveFocus.Count > 0 || toRemoveAttention.Count > 0)
                    SavePointsData();
            }
            catch (Exception ex) { LogError("CleanupPointsData", ex); }
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
                if (currentIds == null || currentIds.Count == 0) return;

                foreach (var id in currentIds)
                    settings.AccumulatedKillsPerHero[id] = 0;
                SaveData();
                if (Campaign.Current != null)
                {
                    TextObject msg = new TextObject("{=JR_ResetAllDone}Just Regeneration: All clan member kill counters reset to 0.");
                    InformationManager.DisplayMessage(new InformationMessage(msg.ToString()));
                }
            }
            catch (Exception ex) { LogError("ResetAllKills", ex); }
        }

        public void ApplyAgeToHeroes(List<Hero> heroes, float age)
        {
            try
            {
                if (heroes == null || heroes.Count == 0) return;
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                foreach (var hero in heroes)
                {
                    if (hero == null) continue;
                    ApplyAgeToHero(hero, age);
                    settings.CustomAges[hero.StringId] = age;
                }
                SaveAgeData();
            }
            catch (Exception ex) { LogError("ApplyAgeToHeroes", ex); }
        }

        public void SetAgingForHeroes(List<Hero> heroes, bool disable)
        {
            try
            {
                if (heroes == null || heroes.Count == 0) return;
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                foreach (var hero in heroes)
                {
                    if (hero == null) continue;
                    string id = hero.StringId;
                    if (disable)
                    {
                        float currentAge = hero.Age;
                        settings.CustomAges[id] = currentAge;
                        settings.DisableAgingFlags[id] = true;
                    }
                    else
                    {
                        settings.CustomAges.Remove(id);
                        settings.DisableAgingFlags.Remove(id);
                    }
                }
                SaveAgeData();
            }
            catch (Exception ex) { LogError("SetAgingForHeroes", ex); }
        }

        // =====================================================================
        // СОХРАНЕНИЕ / ЗАГРУЗКА (атомарная запись через .tmp)
        // =====================================================================

        private static void AtomicWriteAllLines(string path, IEnumerable<string> lines)
        {
            string tmpPath = path + ".tmp";
            File.WriteAllLines(tmpPath, lines);

            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmpPath, path);
        }

        private void SaveData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var lines = settings.AccumulatedKillsPerHero.Select(kvp => $"{kvp.Key}|{kvp.Value}").ToList();
                AtomicWriteAllLines(SaveFilePath, lines);
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
                    Hero mainHero;
                    try { mainHero = Hero.MainHero; }
                    catch { mainHero = null; }

                    if (int.TryParse(lines[0], out int oldKills) && mainHero != null)
                        settings.SetKillsForHero(mainHero, oldKills);
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

        private void LoadAgeData()
        {
            try
            {
                if (!File.Exists(AgeSaveFilePath)) return;
                string[] lines = File.ReadAllLines(AgeSaveFilePath);
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length != 3) continue;

                    string id = parts[0];
                    string type = parts[1];
                    string value = parts[2];

                    if (type == "Age" && float.TryParse(value, out float age))
                        settings.CustomAges[id] = age;
                    else if (type == "Aging" && bool.TryParse(value, out bool disabled))
                        settings.DisableAgingFlags[id] = disabled;
                }
            }
            catch (Exception ex) { LogError("LoadAgeData", ex); }
        }

        private void SaveAgeData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var lines = new List<string>();
                foreach (var kvp in settings.CustomAges)
                    lines.Add($"{kvp.Key}|Age|{kvp.Value}");
                foreach (var kvp in settings.DisableAgingFlags)
                    lines.Add($"{kvp.Key}|Aging|{kvp.Value}");

                AtomicWriteAllLines(AgeSaveFilePath, lines);
            }
            catch (Exception ex) { LogError("SaveAgeData", ex); }
        }

        private void SavePointsData()
        {
            try
            {
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                var lines = new List<string>();
                foreach (var kvp in settings.AccumulatedKillsForFocus)
                    lines.Add($"Focus|{kvp.Key}|{kvp.Value}");
                foreach (var kvp in settings.AccumulatedKillsForAttention)
                    lines.Add($"Attention|{kvp.Key}|{kvp.Value}");

                AtomicWriteAllLines(PointsSaveFilePath, lines);
            }
            catch (Exception ex) { LogError("SavePointsData", ex); }
        }

        private void LoadPointsData()
        {
            try
            {
                if (!File.Exists(PointsSaveFilePath)) return;
                string[] lines = File.ReadAllLines(PointsSaveFilePath);
                var settings = JustRegenerationSettings.Instance;
                if (settings == null) return;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length != 3) continue;

                    string type = parts[0];
                    string id = parts[1];
                    if (!int.TryParse(parts[2], out int kills)) continue;

                    if (type == "Focus")
                        settings.AccumulatedKillsForFocus[id] = kills;
                    else if (type == "Attention")
                        settings.AccumulatedKillsForAttention[id] = kills;
                }
            }
            catch (Exception ex) { LogError("LoadPointsData", ex); }
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
            var settings = JustRegenerationSettings.Instance;
            try { settings?.RefreshHeroList(); } catch (Exception ex) { LogError("OnPlayerCharacterChanged", ex); }
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