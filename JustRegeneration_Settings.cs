using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using MCM.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace JustRegeneration
{
    // Вспомогательный класс для отображения в выпадающем списке
    public class HeroDisplayItem
    {
        public string Id { get; }
        public string DisplayName { get; }

        public HeroDisplayItem(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }

    // =========================================================================
    // 3. НАСТРОЙКИ (MCM) – с использованием MCM.Common.Dropdown и HeroDisplayItem
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
        [SettingPropertyInteger("{=JR_PlayerKillsPerRejuvenation}Kills needed per rejuvenation", 1, 10000, Order = 52, RequireRestart = false, HintText = "{=JR_PlayerKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
        public int PlayerKillsPerRejuvenation { get; set; } = 365;

        [SettingPropertyGroup("{=JR_GroupRejuvenationPlayer}Rejuvenation (Player)")]
        [SettingPropertyInteger("{=JR_PlayerDaysPerRejuvenation}Days per rejuvenation", 1, 365, Order = 53, RequireRestart = false, HintText = "{=JR_PlayerDaysPerRejuvenation_Hint}How many days to rejuvenate when threshold is met.")]
        public int PlayerDaysPerRejuvenation { get; set; } = 365;

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
        [SettingPropertyInteger("{=JR_FamilyKillsPerRejuvenation}Kills needed per rejuvenation", 1, 10000, Order = 62, RequireRestart = false, HintText = "{=JR_FamilyKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
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
        [SettingPropertyInteger("{=JR_CompanionKillsPerRejuvenation}Kills needed per rejuvenation", 1, 10000, Order = 72, RequireRestart = false, HintText = "{=JR_CompanionKillsPerRejuvenation_Hint}Number of kills required to trigger rejuvenation.")]
        public int CompanionKillsPerRejuvenation { get; set; } = 365;

        [SettingPropertyGroup("{=JR_GroupRejuvenationCompanions}Rejuvenation (Companions)")]
        [SettingPropertyInteger("{=JR_CompanionDaysPerRejuvenation}Days per rejuvenation", 1, 365, Order = 73, RequireRestart = false, HintText = "{=JR_CompanionDaysPerRejuvenation_Hint}How many days to rejuvenate when threshold is met.")]
        public int CompanionDaysPerRejuvenation { get; set; } = 365;

        // =====================================================================
        // НОВЫЙ РАЗДЕЛ: СБРОС СЧЁТЧИКОВ УБИЙСТВ (с отображением имён)
        // =====================================================================

        private Dropdown<HeroDisplayItem> _selectedResetHero = new Dropdown<HeroDisplayItem>(new[] { new HeroDisplayItem("all", "All") }, 0);
        [SettingPropertyGroup("{=JR_GroupReset}Reset Kill Counters", GroupOrder = 100)]
        [SettingPropertyDropdown("{=JR_SelectHero}Select hero", Order = 100, RequireRestart = false, HintText = "{=JR_SelectHero_Hint}Select a clan member to view/reset kills.")]
        public Dropdown<HeroDisplayItem> SelectedResetHero
        {
            get => _selectedResetHero;
            set
            {
                if (_selectedResetHero != value)
                {
                    _selectedResetHero = value;
                    OnPropertyChanged(nameof(SelectedResetHero));
                    OnPropertyChanged(nameof(SelectedHeroKillsDisplay));
                }
            }
        }

        // Отображение количества убийств для выбранного героя
        public string SelectedHeroKillsDisplay
        {
            get
            {
                if (SelectedResetHero == null || SelectedResetHero.Count == 0) return "";
                var selectedItem = SelectedResetHero.SelectedValue;
                if (selectedItem == null || selectedItem.Id == "all") return "";
                return MySubModule.Current?.GetKillsForHeroId(selectedItem.Id).ToString() ?? "";
            }
        }

        private bool _resetKillsButton = false;
        [SettingPropertyGroup("{=JR_GroupReset}Reset Kill Counters")]
        [SettingPropertyButton("{=JR_ResetKillsButton}Reset kills", Order = 102, RequireRestart = false, HintText = "{=JR_ResetKillsButton_Hint}Reset kill counter for selected hero or all.")]
        public bool ResetKillsButton
        {
            get => _resetKillsButton;
            set
            {
                if (value)
                {
                    try
                    {
                        if (MySubModule.Current == null) return;
                        var selectedItem = SelectedResetHero.SelectedValue;
                        if (selectedItem == null) return;
                        if (selectedItem.Id == "all")
                            MySubModule.Current.ResetAllKills();
                        else
                            MySubModule.Current.ResetKillsForHeroId(selectedItem.Id);
                        OnPropertyChanged(nameof(SelectedHeroKillsDisplay));
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
                    _resetKillsButton = false;
                    OnPropertyChanged(nameof(ResetKillsButton));
                }
            }
        }

        /// <summary>
        /// Обновляет список героев в выпадающем списке. Вызывается при загрузке кампании.
        /// В отличие от предыдущей версии, мы не создаём новый Dropdown, а изменяем существующий.
        /// </summary>
        public void RefreshHeroList()
        {
            try
            {
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration", "debug.log");
                File.AppendAllText(debugPath, $"{DateTime.Now}: RefreshHeroList called\n");

                var subModule = MySubModule.Current;
                if (subModule == null || Campaign.Current == null || Hero.MainHero == null)
                {
                    File.AppendAllText(debugPath, $"{DateTime.Now}: subModule or Campaign or MainHero is null\n");
                    // Оставляем только "All"
                    SelectedResetHero.Clear();
                    SelectedResetHero.Add(new HeroDisplayItem("all", "All"));
                    SelectedResetHero.SelectedIndex = 0;
                    OnPropertyChanged(nameof(SelectedResetHero));
                    OnPropertyChanged(nameof(SelectedHeroKillsDisplay));
                    return;
                }

                var ids = subModule.GetCurrentClanMemberIds();
                File.AppendAllText(debugPath, $"{DateTime.Now}: ids count = {ids.Count}\n");

                // Сохраняем текущий выбранный ID
                string currentSelectedId = SelectedResetHero.SelectedValue?.Id ?? "all";

                // Очищаем и заполняем заново
                SelectedResetHero.Clear();
                SelectedResetHero.Add(new HeroDisplayItem("all", "All"));

                var heroes = Campaign.Current.AliveHeroes
                    .Where(h => ids.Contains(h.StringId))
                    .OrderBy(h => h.Name.ToString())
                    .ToList();

                foreach (var hero in heroes)
                {
                    // Для игрока добавим пометку (Player)
                    string displayName = hero.Name.ToString();
                    if (hero == Hero.MainHero)
                        displayName = $"{displayName} (Player)";
                    SelectedResetHero.Add(new HeroDisplayItem(hero.StringId, displayName));
                }

                // Восстанавливаем выбранный индекс
                int newIndex = 0;
                for (int i = 0; i < SelectedResetHero.Count; i++)
                {
                    if (SelectedResetHero[i].Id == currentSelectedId)
                    {
                        newIndex = i;
                        break;
                    }
                }
                SelectedResetHero.SelectedIndex = newIndex;

                // Принудительно уведомляем об изменениях
                OnPropertyChanged(nameof(SelectedResetHero));
                OnPropertyChanged(nameof(SelectedHeroKillsDisplay));

                File.AppendAllText(debugPath, $"{DateTime.Now}: SelectedResetHero count = {SelectedResetHero.Count}, selected index = {SelectedResetHero.SelectedIndex}, value = {SelectedResetHero.SelectedValue?.Id}\n");
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "JustRegeneration", "mcm_errors.log");
                    File.AppendAllText(logPath, $"{DateTime.Now}: RefreshHeroList error: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
            }
        }

        // =====================================================================

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
            // Исправляем ошибку – проверяем Hero.MainHero на null
            try
            {
                if (Hero.MainHero != null)
                {
                    // Раньше здесь было обновление текстового поля для игрока, но мы его убрали.
                    // Оставляем пустым, чтобы не вызывать ошибок.
                }
            }
            catch { }
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
}