using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using static AutoPinSigns.AutoPinSigns;
using static Minimap;

namespace AutoPinSigns
{
    internal static class LocalSignPins
    {
        private sealed class SignState
        {
            internal readonly Sign Sign;
            internal readonly Piece Piece;
            internal readonly WearNTear WearNTear;

            internal string RawText = string.Empty;
            internal bool HasMatch;
            internal SignPinMatch Match;
            internal PinData LocalPin;

            internal SignState(Sign sign)
            {
                Sign = sign;
                Piece = sign.GetComponent<Piece>();
                WearNTear = sign.GetComponent<WearNTear>();
            }

            internal void UpdateFromZdo()
            {
                if (!Sign || !Sign.m_nview || !Sign.m_nview.IsValid())
                    return;

                ZDO zdo = Sign.m_nview.GetZDO();
                if (zdo == null)
                    return;

                RawText = zdo.GetString(ZDOVars.s_text);
                HasMatch = SignPinParser.TryParse(RawText, out Match);
                UpdateVisibleText();
                UpdateLocalPin();
            }

            internal void Refresh()
            {
                HasMatch = SignPinParser.TryParse(RawText, out Match);
                UpdateVisibleText();
                UpdateLocalPin();
            }

            internal void UpdateVisibleText()
            {
                if (!Sign || Sign.m_textWidget == null)
                    return;

                string visibleText = IsEnabled && HasMatch ? Match.Name : RawText;
                if (Sign.m_textWidget.text != visibleText)
                    Sign.m_textWidget.SetText(visibleText);
            }

            internal void UpdateLocalPin()
            {
                if (!IsEnabled || ServerPinSync.HasActiveAuthority || !HasMatch || !Minimap.instance || !Sign)
                {
                    RemoveLocalPin();
                    return;
                }

                if (LocalPin != null && !Minimap.instance.m_pins.Contains(LocalPin))
                {
                    claimedLocalPins.Remove(LocalPin);
                    LocalPin = null;
                }

                if (LocalPin != null &&
                    (LocalPin.m_type != Match.Type ||
                     LocalPin.m_name != Match.Name ||
                     Utils.DistanceXZ(LocalPin.m_pos, Sign.transform.position) >= 1f))
                {
                    RemoveLocalPin();
                }

                LocalPin ??= FindExistingSavedPin(Sign.transform.position, Match);
                if (LocalPin != null)
                {
                    claimedLocalPins.Add(LocalPin);
                    return;
                }

                LocalPin = Minimap.instance.AddPin(Sign.transform.position, Match.Type, Match.Name, save: true, isChecked: false);
                if (LocalPin == null)
                    return;

                claimedLocalPins.Add(LocalPin);
                LogInfo($"Added local map pin from sign: \"{Match.Name}\" {LocalPin.m_icon?.name}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "$msg_pin_added: " + Match.Name, 0, LocalPin.m_icon);
            }

            internal void RemoveLocalPin()
            {
                if (LocalPin == null)
                    return;

                claimedLocalPins.Remove(LocalPin);
                if (Minimap.instance && Minimap.instance.m_pins.Contains(LocalPin))
                {
                    LogInfo($"Removed local map pin from sign: \"{LocalPin.m_name}\" {LocalPin.m_icon?.name} {LocalPin.m_pos}");
                    Minimap.instance.RemovePin(LocalPin);
                }

                LocalPin = null;
            }
        }

        private static readonly Dictionary<Sign, SignState> signStates = new();
        private static readonly Dictionary<Piece, SignState> pieceStates = new();
        private static readonly Dictionary<WearNTear, SignState> wearNTearStates = new();
        private static readonly List<SignState> refreshBuffer = new();
        private static readonly List<Piece> nearbyPieces = new();
        private static readonly HashSet<PinData> claimedLocalPins = new();

        private static Vector2i currentZone = new(int.MinValue, int.MinValue);
        private static Minimap observedMinimap;

        internal static void Register(Sign sign)
        {
            if (!sign)
                return;

            if (!signStates.TryGetValue(sign, out SignState state))
            {
                state = new SignState(sign);
                signStates.Add(sign, state);

                if (state.Piece)
                    pieceStates[state.Piece] = state;
                if (state.WearNTear)
                    wearNTearStates[state.WearNTear] = state;
            }

            state.UpdateFromZdo();
            ServerPinSync.RegisterLoadedSign(sign);
        }

        internal static void RefreshAll()
        {
            InvalidateCleanupZone();
            refreshBuffer.Clear();
            foreach (SignState state in signStates.Values)
                refreshBuffer.Add(state);

            for (int i = 0; i < refreshBuffer.Count; ++i)
                refreshBuffer[i].Refresh();

            refreshBuffer.Clear();
        }

        internal static void InvalidateCleanupZone() => currentZone = new Vector2i(int.MinValue, int.MinValue);

        internal static void ClearStates()
        {
            signStates.Clear();
            pieceStates.Clear();
            wearNTearStates.Clear();
            refreshBuffer.Clear();
            nearbyPieces.Clear();
            claimedLocalPins.Clear();
            currentZone = new Vector2i(int.MinValue, int.MinValue);
            observedMinimap = null;
        }

        internal static void Tick()
        {
            if (observedMinimap != Minimap.instance)
            {
                observedMinimap = Minimap.instance;
                if (observedMinimap)
                    RefreshAll();
            }

            TickNearbyCleanup();
        }

        private static void TickNearbyCleanup()
        {
            if (!IsEnabled || ServerPinSync.HasActiveAuthority || removePinsWithoutSigns?.Value != true || !ZNet.instance || !Minimap.instance || !ZoneSystem.instance)
                return;

            Vector2i nextZone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            if (nextZone == currentZone)
                return;

            currentZone = nextZone;
            if (!IsCurrentZoneActive())
                return;

            List<PinData> pins = Minimap.instance.m_pins;
            for (int index = pins.Count - 1; index >= 0; --index)
            {
                PinData pin = pins[index];
                if (!pin.m_save || !IsUserPinType(pin.m_type) || ZoneSystem.GetZone(pin.m_pos) != currentZone || HasRelatedLoadedSign(pin))
                    continue;

                LogInfo($"Removed saved user pin without a matching sign: \"{pin.m_name}\" {pin.m_icon?.name} {pin.m_pos}");
                Minimap.instance.RemovePin(pin);
            }
        }

        private static bool HasRelatedLoadedSign(PinData pin)
        {
            nearbyPieces.Clear();
            Piece.GetAllPiecesInRadius(pin.m_pos, 1f, nearbyPieces);

            for (int i = 0; i < nearbyPieces.Count; ++i)
            {
                if (!pieceStates.TryGetValue(nearbyPieces[i], out SignState state) || !state.HasMatch)
                    continue;

                if (state.Match.Type == pin.m_type && state.Match.Name == pin.m_name)
                    return true;
            }

            return false;
        }

        private static bool IsCurrentZoneActive() =>
            ZoneSystem.instance.IsZoneLoaded(currentZone) &&
            ZoneSystem.instance.m_zones.TryGetValue(currentZone, out var zoneData) &&
            zoneData.m_ttl <= 0.1f;

        private static PinData FindExistingSavedPin(Vector3 position, SignPinMatch match)
        {
            List<PinData> pins = Minimap.instance.m_pins;
            for (int i = 0; i < pins.Count; ++i)
            {
                PinData pin = pins[i];
                if (pin.m_save && !claimedLocalPins.Contains(pin) && pin.m_type == match.Type && pin.m_name == match.Name && Utils.DistanceXZ(position, pin.m_pos) < 1f)
                    return pin;
            }

            return null;
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.Awake))]
        private static class Sign_Awake_Register
        {
            private static void Postfix(Sign __instance) => Register(__instance);
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.OnCheckPermissionCompleted))]
        private static class Sign_OnCheckPermissionCompleted_Update
        {
            private static void Postfix(Sign __instance) => Register(__instance);
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.GetText))]
        private static class Sign_GetText_ReturnOriginalText
        {
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Sign __instance, ref string __result)
            {
                if (IsEnabled && signStates.TryGetValue(__instance, out SignState state))
                    __result = state.RawText;
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        private static class WearNTear_Destroy_RemovePin
        {
            private static void Prefix(WearNTear __instance)
            {
                if (wearNTearStates.TryGetValue(__instance, out SignState state))
                    state.RemoveLocalPin();
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))]
        private static class WearNTear_OnDestroy_Unregister
        {
            private static void Prefix(WearNTear __instance)
            {
                if (!wearNTearStates.TryGetValue(__instance, out SignState state))
                    return;

                claimedLocalPins.Remove(state.LocalPin);
                signStates.Remove(state.Sign);
                if (state.Piece)
                    pieceStates.Remove(state.Piece);
                wearNTearStates.Remove(__instance);
            }
        }


        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapData), new[] { typeof(byte[]) })]
        private static class Minimap_SetMapData_RefreshLocalPins
        {
            private static void Postfix()
            {
                if (IsEnabled && !ServerPinSync.HasActiveAuthority)
                    RefreshAll();
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
        private static class ZoneSystem_OnDestroy_ClearLocalStates
        {
            private static void Prefix() => ClearStates();
        }
    }
}
