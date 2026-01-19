using System;
using System.Linq;
using System.Collections.Generic;
using static Minimap;

namespace AutoPinSigns
{
    public partial class AutoPinSigns
    {
        public class SignState
        {
            public Sign m_sign;
            public PinData m_pin;
            public string m_rawText;
            public string m_displayText;
            public Piece m_piece;
            public WearNTear m_wnt;

            public SignState(Sign sign, string rawText)
            {
                m_sign = sign;
                m_rawText = rawText;
                m_piece = sign.GetComponent<Piece>();
                m_wnt = sign.GetComponent<WearNTear>();

                signStates[m_sign] = this;
                
                if (m_piece)
                    pieceStates[m_piece] = this;

                if (m_wnt)
                    wntStates[m_wnt] = this;

                UpdateMapPin();
            }

            public void UpdateSignText(string rawText)
            {
                if (rawText == m_rawText)
                {
                    if (m_pin != null)
                        UpdateWidgetText();
                }
                else
                {
                    m_rawText = rawText;
                    UpdateMapPin();
                }
            }

            public void UpdateMapPin()
            {
                if (!TryMatchPrefix(m_rawText, out PinType pinType, out m_displayText))
                {
                    if (useStringsList.Value && IsPinnableSign(m_rawText))
                    {
                        pinType = GetIcon(m_rawText);
                        m_displayText = m_rawText;
                    }
                    else
                    {
                        RemoveMapPin();
                        return;
                    }
                }

                m_pin ??= Minimap.instance.m_pins.FirstOrDefault(pin => pin.m_name == m_displayText && pin.m_type == pinType && pin.m_save && Utils.DistanceXZ(m_sign.transform.position, pin.m_pos) < 1f);

                if (m_pin != null && m_pin.m_name != m_displayText)
                    RemoveMapPin();

                if (m_pin == null)
                {
                    m_pin = Minimap.instance.AddPin(
                        m_sign.transform.position,
                        pinType,
                        m_displayText,
                        save: true,
                        isChecked: false
                    );

                    LogInfo($"Added map pin from sign: \"{m_displayText}\" {m_pin.m_icon?.name}");
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.TopLeft,
                        "$msg_pin_added: " + m_displayText,
                        0,
                        m_pin.m_icon
                    );
                }

                UpdateWidgetText();
            }

            public void UpdateWidgetText()
            {
                if (m_sign.m_textWidget?.text != m_displayText)
                    m_sign.m_textWidget?.SetText(m_displayText);
            }

            public void RemoveMapPin()
            {
                if (m_pin == null)
                    return;

                LogInfo($"Removed map pin from sign: \"{m_pin.m_name}\" {m_pin.m_icon?.name} {m_pin.m_pos}");
                Minimap.instance.RemovePin(m_pin);
                m_pin = null;
            }

            private static bool IsSign(HashSet<string> list, string text) => allowSubstrings.Value ? list.Any(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) : list.Contains(text.ToLower());
            private static bool IsFireSign(string text) => IsSign(fireList, text);
            private static bool IsBaseSign(string text) => IsSign(baseList, text);
            private static bool IsHammerSign(string text) => IsSign(hammerList, text);
            private static bool IsPinSign(string text) => IsSign(pinList, text);
            private static bool IsPortalSign(string text) => IsSign(portalList, text);
            private static bool IsPinnableSign(string text) => IsSign(allpins, text);

            private static PinType GetIcon(string text)
            {
                if (IsFireSign(text))
                    return PinType.Icon0;
                if (IsBaseSign(text))
                    return PinType.Icon1;
                if (IsHammerSign(text))
                    return PinType.Icon2;
                if (IsPinSign(text))
                    return PinType.Icon3;
                if (IsPortalSign(text))
                    return PinType.Icon4;

                return PinType.Icon3;
            }

            public static SignState UpdatePinState(Sign sign)
            {
                string rawText = sign.m_nview.GetZDO().GetString(ZDOVars.s_text);
                if (stripHTMLTags.Value)
                    rawText = rawText.RemoveRichTextTags();

                if (signStates.TryGetValue(sign, out SignState state))
                    state.UpdateSignText(rawText);
                else
                    state = new SignState(sign, rawText);

                return state;
            }

            private static bool StartsWithAny(string text, HashSet<string> prefixes, out string prefix)
            {
                foreach (string p in prefixes)
                {
                    if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        prefix = p;
                        return true;
                    }
                }
                prefix = null;
                return false;
            }

            private static bool TryMatchPrefix(string text, out PinType pinType, out string strippedText)
            {
                strippedText = text;
                pinType = PinType.None;

                if (!useStringsPrefix.Value)
                    return false;

                if (StartsWithAny(text, firePrefix, out var pf))
                    pinType = PinType.Icon0;
                else if (StartsWithAny(text, basePrefix, out pf))
                    pinType = PinType.Icon1;
                else if (StartsWithAny(text, hammerPrefix, out pf))
                    pinType = PinType.Icon2;
                else if (StartsWithAny(text, pinPrefix, out pf))
                    pinType = PinType.Icon3;
                else if (StartsWithAny(text, portalPrefix, out pf))
                    pinType = PinType.Icon4;
                else
                    return false;

                strippedText = text.Substring(pf.Length).Trim();
                return strippedText.Length > 0;
            }

        }
    }
}
