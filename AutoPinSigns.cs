using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoPinSigns
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class AutoPinSigns : BaseUnityPlugin
    {
        public const string pluginID = "shudnal.AutoPinSigns";
        public const string pluginName = "Auto Pin Signs";
        public const string pluginVersion = "1.0.8";
        
        private Harmony _harmony;

        private static ConfigEntry<bool> modEnabled;
        private static ConfigEntry<bool> allowSubstrings;

        private static ConfigEntry<string> configFireList;
        private static ConfigEntry<string> configBaseList;
        private static ConfigEntry<string> configHammerList;
        private static ConfigEntry<string> configPinList;
        private static ConfigEntry<string> configPortalList;

        private static Dictionary<Vector3, string> itemsPins = new Dictionary<Vector3, string>();
        private static readonly HashSet<string> fireList = new HashSet<string>();
        private static readonly HashSet<string> baseList = new HashSet<string>();
        private static readonly HashSet<string> hammerList = new HashSet<string>();
        private static readonly HashSet<string> pinList = new HashSet<string>();
        private static readonly HashSet<string> portalList = new HashSet<string>();

        private static AutoPinSigns instance;

        private void Awake()
        {
            ConfigInit();

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), pluginID);

            instance = this;
        }

        private void OnDestroy()
        {
            Config.Save();
            _harmony?.UnpatchSelf();
        }

        private void ConfigInit()
        {
            modEnabled = Config.Bind("General", "Enabled", defaultValue: true, "Enable the mod");
            allowSubstrings = Config.Bind("General", "Less strict string comparison", defaultValue: false, "Less strict comparison of config substrings. Enable to create pins if sign have any substring instead of exact match");

            configFireList = Config.Bind("Signs", "FireList", defaultValue: "fire", "List of the case-insensitive strings to add Fire pin.  Comma-separate each string.  Default: fire");
            configBaseList = Config.Bind("Signs", "BaseList", defaultValue: "base", "List of the case-insensitive strings to add Base pin.  Comma-separate each string.  Default: base");
            configHammerList = Config.Bind("Signs", "HammerList", defaultValue: "hammer", "List of the strings to add Hammer pin.  Comma-separate each string.  Default: hammer");
            configPinList = Config.Bind("Signs", "PinList", defaultValue: "pin,dot", "List of the strings to add Dot pin.  Comma-separate each string.  Default: pin,dot");
            configPortalList = Config.Bind("Signs", "PortalList", defaultValue: "portal", "List of the strings to add Portal pin.  Comma-separate each string.  Default: portal");

            AddToHS(configFireList.Value, fireList);
            AddToHS(configBaseList.Value, baseList);
            AddToHS(configHammerList.Value, hammerList);
            AddToHS(configPinList.Value, pinList);
            AddToHS(configPortalList.Value, portalList);

            InitCommands();
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
                {
                    if (args.Args.Length > 2 && float.TryParse(args.Args[2], out float j))
                        DeleteClosestPins(Player.m_localPlayer.transform.position, j);
                    else
                        DeleteClosestPins(Player.m_localPlayer.transform.position);
                }
                else
                {
                    args.Context.AddString($"Syntax: {typeof(AutoPinSigns).Namespace.ToLower()} [action]");
                }

            }, isCheat: false, isNetwork: false, onlyServer: false, isSecret: false, allowInDevBuild: false, () => new List<string>() { "clear [range] -  Clear closest to current player pins in set range. Default 5" }, alwaysRefreshTabOptions: true, remoteCommand: false);
        }

        private static bool IsSign(HashSet<string> list, string text)
        {
            if (allowSubstrings.Value)
                return list.Any(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
            else
                return list.Contains(text);
        }

        static bool IsFireSign(string text)
        {
            return IsSign(fireList, text);
        }
        static bool IsBaseSign(string text)
        {
            return IsSign(baseList, text);
        }
        static bool IsHammerSign(string text)
        {
            return IsSign(hammerList, text);
        }
        static bool IsPinSign(string text)
        {
            return IsSign(pinList, text);
        }
        static bool IsPortalSign(string text)
        {
            return IsSign(portalList, text);
        }

        static bool IsPinnableSign(string text)
        {
            return IsFireSign(text) || IsBaseSign(text) || IsHammerSign(text) || IsPinSign(text) || IsPortalSign(text);
        }

        static Minimap.PinType GetIcon(string text)
        {
            if (IsFireSign(text))
                return Minimap.PinType.Icon0;
            if (IsBaseSign(text))
                return Minimap.PinType.Icon1;
            if (IsHammerSign(text))
                return Minimap.PinType.Icon2;
            if (IsPinSign(text))
                return Minimap.PinType.Icon3;
            if (IsPortalSign(text))
                return Minimap.PinType.Icon4;

            return Minimap.PinType.Icon3;
        }

        static void AddToHS(string text, HashSet<string> HS)
        {
            HS.Clear();

            char[] separator = new char[1] { ',' };
            foreach (string item in text.Replace(" ", "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                HS.Add(item);
            }
        }

        static public void DeleteClosestPins(Vector3 pos, float distance = 5.0f)
        {
            while (FindAndDeleteClosestPin(pos, distance)) { };

            itemsPins = itemsPins.Where(pin_pos => Utils.DistanceXZ(pos, pin_pos.Key) >= distance).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        static public bool FindAndDeleteClosestPin(Vector3 pos, float distance = 5.0f)
        {
            if (Minimap.instance)
            {
                foreach (Minimap.PinData pin in Minimap.instance.m_pins)
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

        [HarmonyPatch(typeof(Sign), nameof(Sign.UpdateText))]
        public static class Sign_UpdateText_patch
        {
            static void Postfix(Sign __instance)
            {
                if (!modEnabled.Value)
                    return;

                if (!Minimap.instance)
                    return;

                string lowertext = __instance.GetText().RemoveRichTextTags().ToLower();

                Vector3 pos = __instance.transform.position;

                if (itemsPins.ContainsKey(pos))
                {
                    string currenttext = itemsPins[pos];

                    if (!IsPinnableSign(lowertext))
                    {
                        DeleteClosestPins(pos);
                        return;
                    }
                    else if (currenttext != lowertext)
                    {
                        DeleteClosestPins(pos);
                    }
                    else return;
                }

                if (!IsPinnableSign(lowertext))
                    return;

                Minimap.PinType icon = GetIcon(lowertext);

                if (Minimap.instance.HaveSimilarPin(pos, icon, lowertext, true))
                    return;

                Minimap.instance.AddPin(pos, icon, lowertext, true, false, 0L);
                itemsPins.Add(pos, lowertext);

                Sprite m_icon_sprite = Minimap.instance.GetSprite(icon);

                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "$msg_pin_added: " + lowertext, 0, m_icon_sprite);
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        public static class WearNTear_Destroy_patch
        {
            static void Prefix(ref WearNTear __instance, ZNetView ___m_nview)
            {
                if (!modEnabled.Value)
                    return;

                if (!___m_nview || !___m_nview.IsValid())
                    return;

                if (__instance.TryGetComponent(out Sign component) && IsPinnableSign(component.GetText().ToLower()))
                {
                    if (itemsPins.ContainsKey(__instance.transform.position))
                        DeleteClosestPins(__instance.transform.position, 1f);
                }
            }
        }
    }
 }
