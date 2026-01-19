using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Minimap;

namespace AutoPinSigns
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public partial class AutoPinSigns : BaseUnityPlugin
    {
        public const string pluginID = "shudnal.AutoPinSigns";
        public const string pluginName = "Auto Pin Signs";
        public const string pluginVersion = "1.2.0";

        private readonly Harmony harmony = new Harmony(pluginID);

        internal static readonly ConfigSync configSync = new ConfigSync(pluginID) { DisplayName = pluginName, CurrentVersion = pluginVersion, MinimumRequiredVersion = pluginVersion };

        private static ConfigEntry<bool> modEnabled;
        internal static ConfigEntry<bool> configLocked;
        private static ConfigEntry<bool> loggingEnabled;
        private static ConfigEntry<bool> allowSubstrings;
        private static ConfigEntry<bool> removePinsWithoutSigns;
        private static ConfigEntry<bool> stripHTMLTags;

        private static ConfigEntry<bool> useStringsList;
        private static ConfigEntry<string> configFireList;
        private static ConfigEntry<string> configBaseList;
        private static ConfigEntry<string> configHammerList;
        private static ConfigEntry<string> configPinList;
        private static ConfigEntry<string> configPortalList;

        private static ConfigEntry<bool> useStringsPrefix;
        private static ConfigEntry<string> configFirePrefix;
        private static ConfigEntry<string> configBasePrefix;
        private static ConfigEntry<string> configHammerPrefix;
        private static ConfigEntry<string> configPinPrefix;
        private static ConfigEntry<string> configPortalPrefix;

        private static readonly HashSet<string> fireList = new HashSet<string>();
        private static readonly HashSet<string> baseList = new HashSet<string>();
        private static readonly HashSet<string> hammerList = new HashSet<string>();
        private static readonly HashSet<string> pinList = new HashSet<string>();
        private static readonly HashSet<string> portalList = new HashSet<string>();

        private static readonly HashSet<string> firePrefix = new HashSet<string>();
        private static readonly HashSet<string> basePrefix = new HashSet<string>();
        private static readonly HashSet<string> hammerPrefix = new HashSet<string>();
        private static readonly HashSet<string> pinPrefix = new HashSet<string>();
        private static readonly HashSet<string> portalPrefix = new HashSet<string>();


        private static readonly HashSet<string> allpins = new HashSet<string>();

        private static AutoPinSigns instance;

        private void Awake()
        {
            ConfigInit();

            harmony.PatchAll();

            instance = this;

            _ = configSync.AddLockingConfigEntry(configLocked);

            Game.isModded = true;
        }

        private void OnDestroy()
        {
            Config.Save();
            harmony?.UnpatchSelf();
            instance = null;
        }

        private static void LogInfo(object data)
        {
            if (loggingEnabled.Value)
                instance.Logger.LogInfo(data);
        }

        private ConfigDescription GetDescriptionSeparatedStrings(string description) =>
            Chainloader.PluginInfos.ContainsKey("_shudnal.ConfigurationManager")
                    ? new ConfigDescription(description)
                    : new ConfigDescription(description, null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") });

        private void ConfigInit()
        {
            modEnabled = config("General", "Enabled", defaultValue: true, "Enable the mod");
            configLocked = config("General", "Lock Configuration", defaultValue: true, "Configuration is locked and can be changed by server admins only.");
            loggingEnabled = config("General", "Logging enabled", defaultValue: false, "Enable logging. [Not Synced with Server]", false);
            removePinsWithoutSigns = config("General", "Remove nearby map pins without related signs", defaultValue: false, "If enabled - if nearby pin has no related sign that pin will be removed from map.");
            allowSubstrings = config("General", "Less strict string comparison", defaultValue: true, "Enable to create pins if the sign text contains any configured word instead of requiring an exact match.");
            stripHTMLTags = config("General", "Strip HTML tags from text", defaultValue: true, "Should sign text be stripped of HTML tags before string comparison. Disable this is you want HTML prefixes.");

            useStringsPrefix = config("Matching Mode", "Use prefix matching", defaultValue: true,
                "If enabled, the mod checks whether sign text starts with a configured prefix (e.g. \"Pin: \"). " +
                "Prefix text is NOT included in the pin name or visible sign text, but is preserved when editing the sign."
            );

            useStringsList = config("Matching Mode", "Use string list matching", defaultValue: true,
                "If enabled, the mod compares the full sign text against configured word lists. " +
                "The full sign text becomes the pin name."
            );

            configFireList = config("Signs", "FireList", defaultValue: "fire", GetDescriptionSeparatedStrings("List of the case-insensitive strings to add Fire pin. Comma-separate each string."));
            configBaseList = config("Signs", "BaseList", defaultValue: "base,shelter,home,house", GetDescriptionSeparatedStrings("List of the case-insensitive strings to add Base pin. Comma-separate each string."));
            configHammerList = config("Signs", "HammerList", defaultValue: "hammer,crypt,mine,boss,cave", GetDescriptionSeparatedStrings("List of the case-insensitive strings to add Hammer pin. Comma-separate each string."));
            configPinList = config("Signs", "PinList", defaultValue: "pin,dot,ore,vein,point", GetDescriptionSeparatedStrings("List of the case-insensitive strings to add Dot pin. Comma-separate each string."));
            configPortalList = config("Signs", "PortalList", defaultValue: "portal", GetDescriptionSeparatedStrings("List of the case-insensitive strings to add Portal pin. Comma-separate each string."));

            configFireList.SettingChanged += ConfigList_SettingChanged;
            configBaseList.SettingChanged += ConfigList_SettingChanged;
            configHammerList.SettingChanged += ConfigList_SettingChanged;
            configPinList.SettingChanged += ConfigList_SettingChanged;
            configPortalList.SettingChanged += ConfigList_SettingChanged;

            configFirePrefix = config("Signs - Prefixes", "Fire", defaultValue: "Fire:", GetDescriptionSeparatedStrings("List of the case-insensitive prefixes to add Fire pin and omit prefix symbols. Comma-separate each string."));
            configBasePrefix = config("Signs - Prefixes", "Base", defaultValue: "Base:", GetDescriptionSeparatedStrings("List of the case-insensitive prefixes to add Base pin and omit prefix symbols. Comma-separate each string."));
            configHammerPrefix = config("Signs - Prefixes", "Hammer", defaultValue: "Hammer:", GetDescriptionSeparatedStrings("List of the case-insensitive prefixes to add Hammer pin and omit prefix symbols. Comma-separate each string."));
            configPinPrefix = config("Signs - Prefixes", "Pin", defaultValue: "Pin:", GetDescriptionSeparatedStrings("List of the case-insensitive prefixes to add Dot pin and omit prefix symbols. Comma-separate each string."));
            configPortalPrefix = config("Signs - Prefixes", "Portal", defaultValue: "Portal:", GetDescriptionSeparatedStrings("List of the case-insensitive prefixes to add Portal pin and omit prefix symbols. Comma-separate each string."));

            configFirePrefix.SettingChanged += ConfigList_SettingChanged;
            configBasePrefix.SettingChanged += ConfigList_SettingChanged;
            configHammerPrefix.SettingChanged += ConfigList_SettingChanged;
            configPinPrefix.SettingChanged += ConfigList_SettingChanged;
            configPortalPrefix.SettingChanged += ConfigList_SettingChanged;

            UpdatePinLists();

            InitCommands();
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, defaultValue, description);

            SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, string description, bool synchronizedSetting = true) => config(group, name, defaultValue, new ConfigDescription(description), synchronizedSetting);

        private void ConfigList_SettingChanged(object sender, EventArgs e) => UpdatePinLists();

        private static void UpdatePinLists()
        {
            AddToHS(configFireList.Value, fireList);
            AddToHS(configBaseList.Value, baseList);
            AddToHS(configHammerList.Value, hammerList);
            AddToHS(configPinList.Value, pinList);
            AddToHS(configPortalList.Value, portalList);

            allpins.Clear();
            allpins.UnionWith(fireList);
            allpins.UnionWith(baseList);
            allpins.UnionWith(hammerList);
            allpins.UnionWith(pinList);
            allpins.UnionWith(portalList);

            AddToHS(configFirePrefix.Value, firePrefix);
            AddToHS(configBasePrefix.Value, basePrefix);
            AddToHS(configHammerPrefix.Value, hammerPrefix);
            AddToHS(configPinPrefix.Value, pinPrefix);
            AddToHS(configPortalPrefix.Value, portalPrefix);

            signStates.Do(kvp => kvp.Value.UpdateMapPin());
        }

        static void AddToHS(string text, HashSet<string> hashSet)
        {
            hashSet.Clear();
            hashSet.UnionWith(text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.ToLower().Trim()));
        }

        public static void InitCommands()
        {
            new Terminal.ConsoleCommand($"{typeof(AutoPinSigns).Namespace.ToLower()}", "[action]", delegate (Terminal.ConsoleEventArgs args)
            {
                if (!modEnabled.Value)
                {
                    args.Context.AddString("Mod disabled");
                    return;
                }

                if (!Player.m_localPlayer)
                    return;

                if (args.Args.Length >= 2 && args.Args[1] == "clear")
                    while (FindAndDeleteClosestPin(Player.m_localPlayer.transform.position, args.Args.Length > 2 && float.TryParse(args.Args[2], out float j) ? j : 0)) { }
                else
                    args.Context.AddString($"Syntax: {typeof(AutoPinSigns).Namespace.ToLower()} [action]");

            }, isCheat: false, isNetwork: false, onlyServer: false, isSecret: false, allowInDevBuild: false, () => new List<string>() { "clear [range] -  Clear closest to current player pins in set range. Default 5" }, alwaysRefreshTabOptions: true, remoteCommand: false);

            static bool FindAndDeleteClosestPin(Vector3 pos, float distance = 5.0f)
            {
                if (Minimap.instance)
                {
                    foreach (PinData pin in Minimap.instance.m_pins)
                    {
                        if (Utils.DistanceXZ(pos, pin.m_pos) < distance)
                        {
                            Minimap.instance.RemovePin(pin);
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private static bool IsAutoPinIcon(PinType pinType) => pinType == PinType.Icon0
                                                   || pinType == PinType.Icon1
                                                   || pinType == PinType.Icon2
                                                   || pinType == PinType.Icon3
                                                   || pinType == PinType.Icon4;

        private static readonly List<Piece> tempPieces = new List<Piece>();
        private static Vector2i currentZone = Vector2i.zero;

        void FixedUpdate()
        {
            if (!(modEnabled.Value && removePinsWithoutSigns.Value))
                return;

            if (!ZNet.instance)
                return;

            if (currentZone == (currentZone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition())))
                return;
            
            if (!Minimap.instance || !IsCurrentZoneActive())
                return;

            foreach (PinData pin in Minimap.instance.m_pins.Where(IsPinToRemove).ToList())
            {
                LogInfo($"Removed map pin without sign: \"{pin.m_name}\" {pin.m_icon?.name} {pin.m_pos}");
                Minimap.instance.RemovePin(pin);
            }
        }

        private static bool IsPinToRemove(PinData pin)
        {
            if (pin.m_ownerID == 0L && pin.m_save && IsAutoPinIcon(pin.m_type) && currentZone == ZoneSystem.GetZone(pin.m_pos))
            {
                tempPieces.Clear();
                Piece.GetAllPiecesInRadius(pin.m_pos, 1f, tempPieces);
                return !tempPieces.Any(pieceStates.ContainsKey);
            }

            return false;
        }

        private static bool IsCurrentZoneActive() => ZoneSystem.instance && ZoneSystem.instance.IsZoneLoaded(currentZone) && ZoneSystem.instance.m_zones.TryGetValue(currentZone, out var zoneData) && zoneData.m_ttl <= 0.1f;

        public static readonly Dictionary<Sign, SignState> signStates = new Dictionary<Sign, SignState>();
        public static readonly Dictionary<Piece, SignState> pieceStates = new Dictionary<Piece, SignState>();
        public static readonly Dictionary<WearNTear, SignState> wntStates = new Dictionary<WearNTear, SignState>();

        [HarmonyPatch(typeof(Sign), nameof(Sign.OnCheckPermissionCompleted))]
        public static class Sign_OnCheckPermissionCompleted_UpdateSignState
        {
            public static void Postfix(Sign __instance)
            {
                if (!modEnabled.Value)
                    return;

                SignState.UpdatePinState(__instance);
            }
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.GetText))]
        public static class Sign_GetText_GetFullText
        {
            [HarmonyPriority(Priority.First)]
            public static void Postfix(Sign __instance, ref string __result)
            {
                if (!modEnabled.Value)
                    return;

                if (signStates.TryGetValue(__instance, out SignState state))
                    __result = state.m_rawText;
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        public static class WearNTear_Destroy_RemoveAddedPin
        {
            public static void Prefix(WearNTear __instance)
            {
                if (wntStates.TryGetValue(__instance, out SignState signState))
                    signState.RemoveMapPin();
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))]
        public static class WearNTear_OnDestroy_RemoveSignState
        {
            public static void Prefix(WearNTear __instance)
            {
                if (wntStates.TryGetValue(__instance, out SignState signState))
                {
                    signStates.Remove(signState.m_sign);
                    pieceStates.Remove(signState.m_piece);
                    wntStates.Remove(signState.m_wnt);
                }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
        public static class ZoneSystem_OnDestroy_Clear
        {
            public static void Prefix()
            {
                signStates.Clear();
                pieceStates.Clear();
                wntStates.Clear();
            }
        }
    }
}
