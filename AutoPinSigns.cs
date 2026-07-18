using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using ConditionalConfigSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static Minimap;

namespace AutoPinSigns
{
    [Flags]
    internal enum AuthoritativePinTypes
    {
        None = 0,
        Fire = 1 << 0,
        Base = 1 << 1,
        Hammer = 1 << 2,
        Dot = 1 << 3,
        Portal = 1 << 4,
        All = Fire | Base | Hammer | Dot | Portal
    }

    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    [BepInDependency("_shudnal.ConditionalConfigSync", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class AutoPinSigns : BaseUnityPlugin
    {
        public const string pluginID = "shudnal.AutoPinSigns";
        public const string pluginName = "Auto Pin Signs";
        public const string pluginVersion = "2.0.0";

        private static readonly Harmony harmony = new(pluginID);

        internal static readonly ConfigSync configSync = new(pluginID)
        {
            DisplayName = pluginName,
            CurrentVersion = pluginVersion,
            MinimumRequiredVersion = pluginVersion
        };

        internal static ConfigEntry<bool> modEnabled;
        internal static ConfigEntry<bool> configLocked;
        internal static ConfigEntry<bool> loggingEnabled;
        internal static ConfigEntry<bool> allowSubstrings;
        internal static ConfigEntry<bool> removePinsWithoutSigns;
        internal static ConfigEntry<bool> stripHTMLTags;

        internal static ConfigEntry<bool> useStringsList;
        internal static ConfigEntry<string> configFireList;
        internal static ConfigEntry<string> configBaseList;
        internal static ConfigEntry<string> configHammerList;
        internal static ConfigEntry<string> configPinList;
        internal static ConfigEntry<string> configPortalList;

        internal static ConfigEntry<bool> useStringsPrefix;
        internal static ConfigEntry<string> configFirePrefix;
        internal static ConfigEntry<string> configBasePrefix;
        internal static ConfigEntry<string> configHammerPrefix;
        internal static ConfigEntry<string> configPinPrefix;
        internal static ConfigEntry<string> configPortalPrefix;

        internal static ConfigEntry<bool> useStringsSuffix;
        internal static ConfigEntry<string> configFireSuffix;
        internal static ConfigEntry<string> configBaseSuffix;
        internal static ConfigEntry<string> configHammerSuffix;
        internal static ConfigEntry<string> configPinSuffix;
        internal static ConfigEntry<string> configPortalSuffix;

        internal static ConfigEntry<bool> serverAuthoritativePins;
        internal static ConfigEntry<AuthoritativePinTypes> serverAuthoritativePinTypes;
        internal static ConfigEntry<bool> serverAuthoritativeAdminsOnly;
        internal static ConfigEntry<float> serverAuthoritativeMergeDistance;
        internal static ConfigEntry<bool> allowCheckedPinStatus;

        private static AutoPinSigns instance;

        private void Awake()
        {
            instance = this;
            CustomConfigs.Awake();
            ConfigInit();
            SignPinParser.RebuildRules();

            _ = configSync.AddLockingConfigEntry(configLocked);
            harmony.PatchAll();

            InitCommands();
            Game.isModded = true;
        }

        private void FixedUpdate()
        {
            ServerPinSync.Tick();
            LocalSignPins.Tick();
        }

        private void OnDestroy()
        {
            ServerPinSync.ResetSession();
            LocalSignPins.ClearStates();
            harmony.UnpatchSelf();
            Config.Save();
            instance = null;
        }

        internal static bool IsEnabled => modEnabled?.Value == true;

        internal static bool IsAuthoritativeMode => IsEnabled && serverAuthoritativePins?.Value == true;

        internal static bool IsUserPinType(PinType pinType) =>
            pinType == PinType.Icon0 ||
            pinType == PinType.Icon1 ||
            pinType == PinType.Icon2 ||
            pinType == PinType.Icon3 ||
            pinType == PinType.Icon4;

        internal static AuthoritativePinTypes GetPinTypeFlag(PinType pinType) => pinType switch
        {
            PinType.Icon0 => AuthoritativePinTypes.Fire,
            PinType.Icon1 => AuthoritativePinTypes.Base,
            PinType.Icon2 => AuthoritativePinTypes.Hammer,
            PinType.Icon3 => AuthoritativePinTypes.Dot,
            PinType.Icon4 => AuthoritativePinTypes.Portal,
            _ => AuthoritativePinTypes.None
        };

        internal static AuthoritativePinTypes GetConfiguredAuthoritativePinTypes() =>
            (serverAuthoritativePinTypes?.Value ?? AuthoritativePinTypes.All) & AuthoritativePinTypes.All;

        internal static bool IsPinTypeEnabled(AuthoritativePinTypes types, PinType pinType) =>
            (types & GetPinTypeFlag(pinType)) != 0;

        internal static void LogInfo(object data)
        {
            if (loggingEnabled?.Value == true && instance != null)
                instance.Logger.LogInfo(data);
        }

        internal static void LogWarning(object data)
        {
            if (instance != null)
                instance.Logger.LogWarning(data);
        }

        private ConfigDescription GetDescriptionSeparatedStrings(string description) =>
            Chainloader.PluginInfos.ContainsKey("_shudnal.ConfigurationManager")
                ? new ConfigDescription(description)
                : new ConfigDescription(description, null, new CustomConfigs.ConfigurationManagerAttributes
                {
                    CustomDrawer = CustomConfigs.DrawSeparatedStrings(",")
                });

        private void ConfigInit()
        {
            modEnabled = ConfigEntry("General", "Enabled", true, "Enable the mod.");
            configLocked = ConfigEntry("General", "Lock Configuration", true, "Configuration is locked and can be changed by server administrators only.");
            loggingEnabled = ConfigEntry("General", "Logging enabled", false, "Enable diagnostic logging. [Not synchronized with server]", synchronizedSetting: false);
            removePinsWithoutSigns = ConfigEntry("General", "Remove nearby map pins without related signs", false,
                "Remove saved user pins in the currently loaded zone when no matching sign exists near the pin. Server-controlled pin types are never handled by this cleanup.");
            allowSubstrings = ConfigEntry("General", "Less strict string comparison", true,
                "After exact list and explicit prefix/suffix matching, allow the longest configured list value to occur anywhere in the sign text.");
            stripHTMLTags = ConfigEntry("General", "Strip HTML tags from text", true,
                "Strip rich-text tags before matching and before creating the pin name. The original sign text is still preserved for editing.");
            allowCheckedPinStatus = ConfigEntry("General", "Allow checked pin status", true,
                "Allow Shift + E on a recognized pinned sign to toggle its checked status. The state is stored in the sign ZDO and synchronized with authoritative pins.");

            useStringsList = ConfigEntry("Matching Mode", "Use string list matching", true,
                "First match exact full sign text against the configured lists; optionally use the longest partial list match after explicit prefixes and suffixes. The full processed sign text becomes the pin name.");
            useStringsPrefix = ConfigEntry("Matching Mode", "Use prefix matching", true,
                "Match explicit configured prefixes after exact list matching. Example: \"Pin: Boat\" creates a pin named \"Boat\". " +
                $"Use the reserved token {SignPinParser.AnyPinToken} only as the final prefix fallback.");
            useStringsSuffix = ConfigEntry("Matching Mode", "Use suffix matching", true,
                "Match explicit configured suffixes after prefixes. Example: \"Boat here\" with suffix \"here\" creates a pin named \"Boat\". " +
                $"Use the reserved token {SignPinParser.AnyPinToken} only as the final suffix fallback.");

            serverAuthoritativePins = ConfigEntry("Server Authoritative Pins", "Enabled", false,
                "Use the server's sign-derived pin list as the authoritative source for selected standard user pin types. " +
                "Existing client pins using controlled types are temporarily hidden but remain unchanged in the player profile. " +
                "They return when server authority or the corresponding controlled type is disabled. Server pins are not saved to the player profile. " +
                "Pings, events, player pins, location pins and every other pin type are not changed.");
            serverAuthoritativePinTypes = ConfigEntry("Server Authoritative Pins", "Controlled pin types", AuthoritativePinTypes.All,
                "Standard user pin types displayed from the server snapshot while authority is active. Types not selected remain client-controlled.");
            serverAuthoritativeAdminsOnly = ConfigEntry("Server Authoritative Pins", "Only administrator signs", false,
                "Only publish signs whose stored author is present in the server administrator list. Host-authored signs are allowed.");
            serverAuthoritativeMergeDistance = ConfigEntry("Server Authoritative Pins", "Merge identical pins within distance", 0f,
                new ConfigDescription(
                    "Merge pins with the same parsed type and name when they are within this horizontal distance in meters. " +
                    "Candidates are ordered by ZDOID and the first one is retained. Set to 0 to disable merging.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            configFireList = ConfigEntry("Signs", "FireList", "fire", GetDescriptionSeparatedStrings("Case-insensitive words for Fire pins. Separate values with commas."));
            configBaseList = ConfigEntry("Signs", "BaseList", "base,shelter,home,house", GetDescriptionSeparatedStrings("Case-insensitive words for Base pins. Separate values with commas."));
            configHammerList = ConfigEntry("Signs", "HammerList", "hammer,crypt,mine,boss,cave", GetDescriptionSeparatedStrings("Case-insensitive words for Hammer pins. Separate values with commas."));
            configPinList = ConfigEntry("Signs", "PinList", "pin,dot,ore,vein,point", GetDescriptionSeparatedStrings("Case-insensitive words for Dot pins. Separate values with commas."));
            configPortalList = ConfigEntry("Signs", "PortalList", "portal", GetDescriptionSeparatedStrings("Case-insensitive words for Portal pins. Separate values with commas."));

            configFirePrefix = ConfigEntry("Signs - Prefixes", "Fire", "Fire:", GetDescriptionSeparatedStrings(PrefixDescription("Fire")));
            configBasePrefix = ConfigEntry("Signs - Prefixes", "Base", "Base:", GetDescriptionSeparatedStrings(PrefixDescription("Base")));
            configHammerPrefix = ConfigEntry("Signs - Prefixes", "Hammer", "Hammer:", GetDescriptionSeparatedStrings(PrefixDescription("Hammer")));
            configPinPrefix = ConfigEntry("Signs - Prefixes", "Pin", "Pin:", GetDescriptionSeparatedStrings(PrefixDescription("Dot")));
            configPortalPrefix = ConfigEntry("Signs - Prefixes", "Portal", "Portal:", GetDescriptionSeparatedStrings(PrefixDescription("Portal")));

            configFireSuffix = ConfigEntry("Signs - Suffixes", "Fire", "", GetDescriptionSeparatedStrings(SuffixDescription("Fire")));
            configBaseSuffix = ConfigEntry("Signs - Suffixes", "Base", "", GetDescriptionSeparatedStrings(SuffixDescription("Base")));
            configHammerSuffix = ConfigEntry("Signs - Suffixes", "Hammer", "", GetDescriptionSeparatedStrings(SuffixDescription("Hammer")));
            configPinSuffix = ConfigEntry("Signs - Suffixes", "Pin", "", GetDescriptionSeparatedStrings(SuffixDescription("Dot")));
            configPortalSuffix = ConfigEntry("Signs - Suffixes", "Portal", "", GetDescriptionSeparatedStrings(SuffixDescription("Portal")));

            modEnabled.SettingChanged += OnModeSettingChanged;
            serverAuthoritativePins.SettingChanged += OnModeSettingChanged;
            serverAuthoritativePinTypes.SettingChanged += OnAuthoritativeSettingChanged;
            serverAuthoritativeAdminsOnly.SettingChanged += OnAuthoritativeSettingChanged;
            serverAuthoritativeMergeDistance.SettingChanged += OnAuthoritativeSettingChanged;
            allowCheckedPinStatus.SettingChanged += OnCheckedStatusSettingChanged;
            removePinsWithoutSigns.SettingChanged += OnCleanupSettingChanged;

            allowSubstrings.SettingChanged += OnMatchingSettingChanged;
            stripHTMLTags.SettingChanged += OnMatchingSettingChanged;
            useStringsList.SettingChanged += OnMatchingSettingChanged;
            useStringsPrefix.SettingChanged += OnMatchingSettingChanged;
            useStringsSuffix.SettingChanged += OnMatchingSettingChanged;

            configFireList.SettingChanged += OnMatchingSettingChanged;
            configBaseList.SettingChanged += OnMatchingSettingChanged;
            configHammerList.SettingChanged += OnMatchingSettingChanged;
            configPinList.SettingChanged += OnMatchingSettingChanged;
            configPortalList.SettingChanged += OnMatchingSettingChanged;

            configFirePrefix.SettingChanged += OnMatchingSettingChanged;
            configBasePrefix.SettingChanged += OnMatchingSettingChanged;
            configHammerPrefix.SettingChanged += OnMatchingSettingChanged;
            configPinPrefix.SettingChanged += OnMatchingSettingChanged;
            configPortalPrefix.SettingChanged += OnMatchingSettingChanged;

            configFireSuffix.SettingChanged += OnMatchingSettingChanged;
            configBaseSuffix.SettingChanged += OnMatchingSettingChanged;
            configHammerSuffix.SettingChanged += OnMatchingSettingChanged;
            configPinSuffix.SettingChanged += OnMatchingSettingChanged;
            configPortalSuffix.SettingChanged += OnMatchingSettingChanged;
        }

        private static string PrefixDescription(string pinName) =>
            $"Case-insensitive prefixes for {pinName} pins. The matched prefix is omitted from the pin name and visible sign text. " +
            $"Separate values with commas. Use {SignPinParser.AnyPinToken} to match any non-empty sign text.";

        private static string SuffixDescription(string pinName) =>
            $"Case-insensitive suffixes for {pinName} pins. The matched suffix is omitted from the pin name and visible sign text. " +
            $"Separate values with commas. Use {SignPinParser.AnyPinToken} to match any non-empty sign text.";

        private ConfigEntry<T> ConfigEntry<T>(string group, string name, T defaultValue, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> entry = Config.Bind(group, name, defaultValue, description);
            SyncedConfigEntry<T> syncedEntry = configSync.AddConfigEntry(entry);
            syncedEntry.SynchronizedConfig = synchronizedSetting;
            return entry;
        }

        private ConfigEntry<T> ConfigEntry<T>(string group, string name, T defaultValue, string description, bool synchronizedSetting = true) =>
            ConfigEntry(group, name, defaultValue, new ConfigDescription(description), synchronizedSetting);

        private static void OnMatchingSettingChanged(object sender, EventArgs args)
        {
            SignPinParser.RebuildRules();
            LocalSignPins.RefreshAll();
            ServerPinSync.MarkDirty();
        }

        private static void OnModeSettingChanged(object sender, EventArgs args) => ServerPinSync.OnModeChanged();

        private static void OnAuthoritativeSettingChanged(object sender, EventArgs args)
        {
            LocalSignPins.RefreshAll();
            ServerPinSync.OnAuthoritativeSettingsChanged();
        }

        private static void OnCheckedStatusSettingChanged(object sender, EventArgs args)
        {
            LocalSignPins.RefreshAll();
            ServerPinSync.MarkDirty(immediate: true);
        }

        private static void OnCleanupSettingChanged(object sender, EventArgs args) => LocalSignPins.InvalidateCleanupZone();

        private static void InitCommands()
        {
            _ = new Terminal.ConsoleCommand(
                typeof(AutoPinSigns).Namespace.ToLowerInvariant(),
                "clear [range] | status | resync",
                args =>
                {
                    if (args.Args.Length < 2)
                    {
                        PrintCommandSyntax(args);
                        return;
                    }

                    switch (args.Args[1].ToLowerInvariant())
                    {
                        case "clear":
                            if (!IsEnabled)
                            {
                                args.Context.AddString("Auto Pin Signs is disabled.");
                                return;
                            }

                            if (!Player.m_localPlayer)
                            {
                                args.Context.AddString("The clear command requires a local player and map.");
                                return;
                            }

                            float range = 5f;
                            if (args.Args.Length > 2 && !TryParseFloat(args.Args[2], out range))
                            {
                                args.Context.AddString("Invalid range.");
                                return;
                            }

                            range = Mathf.Max(0f, range);
                            int removed = RemoveSavedUserPins(Player.m_localPlayer.transform.position, range);
                            args.Context.AddString($"Removed {removed} saved user pin(s) within {range:0.##} meters.");
                            break;

                        case "status":
                            args.Context.AddString(ServerPinSync.GetStatusText());
                            break;

                        case "resync":
                            args.Context.AddString(ServerPinSync.ForceResync());
                            break;

                        default:
                            PrintCommandSyntax(args);
                            break;
                    }
                },
                isCheat: false,
                isNetwork: false,
                onlyServer: false,
                isSecret: false,
                allowInDevBuild: false,
                () => new List<string>
                {
                    "clear [range] - remove saved standard user pins near the player (default range: 5)",
                    "status - show synchronization status",
                    "resync - refresh local pins or request/rebuild the authoritative list"
                },
                alwaysRefreshTabOptions: true,
                remoteCommand: false);
        }

        private static void PrintCommandSyntax(Terminal.ConsoleEventArgs args) =>
            args.Context.AddString("Syntax: autopinsigns clear [range] | status | resync");

        private static bool TryParseFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
            float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

        private static int RemoveSavedUserPins(Vector3 position, float range)
        {
            if (!Minimap.instance)
                return 0;

            int removed = 0;
            List<PinData> pins = Minimap.instance.m_pins;
            for (int i = pins.Count - 1; i >= 0; --i)
            {
                PinData pin = pins[i];
                if (!pin.m_save || !IsUserPinType(pin.m_type) || ServerPinSync.ControlsPinType(pin.m_type) || Utils.DistanceXZ(position, pin.m_pos) >= range)
                    continue;

                Minimap.instance.RemovePin(pin);
                ++removed;
            }

            return removed;
        }
    }
}
