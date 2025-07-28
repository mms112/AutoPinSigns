using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using static Minimap;
using ServerSync;

namespace AutoPinSigns
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public partial class AutoPinSigns : BaseUnityPlugin
    {
        public const string pluginID = "shudnal.AutoPinSigns";
        public const string pluginName = "Auto Pin Signs";
        public const string pluginVersion = "1.1.0";

        internal static readonly ConfigSync configSync = new ConfigSync(pluginID) { DisplayName = pluginName, CurrentVersion = pluginVersion, MinimumRequiredVersion = pluginVersion };

        private Harmony _harmony;

        private static ConfigEntry<bool> modEnabled;
        private static ConfigEntry<bool> configLocked;
        private static ConfigEntry<bool> loggingEnabled;
        private static ConfigEntry<bool> allowSubstrings;
        private static ConfigEntry<bool> removePinsWithoutSigns;

        private static ConfigEntry<string> configFireList;
        private static ConfigEntry<string> configBaseList;
        private static ConfigEntry<string> configHammerList;
        private static ConfigEntry<string> configPinList;
        private static ConfigEntry<string> configPortalList;
        private static ConfigEntry<string> configCheckedList;

        private static readonly HashSet<string> fireList = new HashSet<string>();
        private static readonly HashSet<string> baseList = new HashSet<string>();
        private static readonly HashSet<string> hammerList = new HashSet<string>();
        private static readonly HashSet<string> pinList = new HashSet<string>();
        private static readonly HashSet<string> portalList = new HashSet<string>();
        private static readonly HashSet<string> checkedList = new HashSet<string>();

        private static readonly HashSet<string> allpins = new HashSet<string>();

        private static AutoPinSigns instance;

        private void Awake()
        {
            ConfigInit();

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), pluginID);

            instance = this;
        }

        private void OnDestroy()
        {
            //Config.Save(); Do not save the config, to keep the synced values
            _harmony?.UnpatchSelf();
        }

        private static void LogInfo(object data)
        {
            if (loggingEnabled.Value)
                instance.Logger.LogInfo(data);
        }

        private void ConfigInit()
        {
            modEnabled = config("General", "Enabled", defaultValue: true, "Enable the mod");
            configLocked = config("General", "LockConfiguration", true, "Configuration is locked and can be changed by server admins only.");
            loggingEnabled = config("General", "Logging enabled", defaultValue: false, "Enable logging", false);
            allowSubstrings = config("General", "Less strict string comparison", defaultValue: false, "Less strict comparison of config substrings. Enable to create pins if sign have any substring instead of exact match");
            removePinsWithoutSigns = config("General", "Remove nearby map pins without related signs", defaultValue: false, "If enabled - if nearby pin has no related sign that pin will be removed from map.", false);


            configFireList = config("Signs", "FireList", defaultValue: "fire", new ConfigDescription("List of the case-insensitive strings to add Fire pin. Comma-separate each string.", 
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));
            configBaseList = config("Signs", "BaseList", defaultValue: "base,shelter,home,house", new ConfigDescription("List of the case-insensitive strings to add Base pin. Comma-separate each string.", 
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));
            configHammerList = config("Signs", "HammerList", defaultValue: "hammer,crypt,mine,boss,cave", new ConfigDescription("List of the strings to add Hammer pin. Comma-separate each string.", 
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));
            configPinList = config("Signs", "PinList", defaultValue: "pin,dot,ore,vein,point", new ConfigDescription("List of the strings to add Dot pin. Comma-separate each string.", 
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));
            configPortalList = config("Signs", "PortalList", defaultValue: "portal", new ConfigDescription("List of the strings to add Portal pin. Comma-separate each string.", 
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));
            configCheckedList = config("Signs", "CheckedList", defaultValue: "(x)", new ConfigDescription("List of the strings to consider this pin checked. Comma-separate each string.",
                                                                                    null, new CustomConfigs.ConfigurationManagerAttributes { CustomDrawer = CustomConfigs.DrawSeparatedStrings(",") }));

            configFireList.SettingChanged += ConfigList_SettingChanged;
            configBaseList.SettingChanged += ConfigList_SettingChanged;
            configHammerList.SettingChanged += ConfigList_SettingChanged;
            configPinList.SettingChanged += ConfigList_SettingChanged;
            configPortalList.SettingChanged += ConfigList_SettingChanged;
            configCheckedList.SettingChanged += ConfigList_SettingChanged;

            configSync.AddLockingConfigEntry(configLocked);

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
            AddToHS(configCheckedList.Value, checkedList);

            allpins.Clear();
            allpins.UnionWith(fireList);
            allpins.UnionWith(baseList);
            allpins.UnionWith(hammerList);
            allpins.UnionWith(pinList);
            allpins.UnionWith(portalList);

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

        [HarmonyPatch(typeof(Sign), nameof(Sign.UpdateText))]
        public static class Sign_UpdateText_UpdateSignState
        {
            public static void Postfix(Sign __instance)
            {
                if (!modEnabled.Value)
                    return;

                SignState.UpdatePinState(__instance);
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        public static class WearNTear_Destroy_RemoveAddedPin
        {
            public static void Prefix(WearNTear __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (wntStates.TryGetValue(__instance, out SignState signState))
                    signState.RemoveMapPin();
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))]
        public static class WearNTear_OnDestroy_RemoveSignState
        {
            public static void Prefix(WearNTear __instance)
            {
                if (!modEnabled.Value)
                    return;

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
