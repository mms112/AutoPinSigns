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
            public string m_text;
            public Piece m_piece;
            public WearNTear m_wnt;

            public SignState(Sign sign, string text)
            {
                m_sign = sign;
                m_text = text;
                m_piece = sign.GetComponent<Piece>();
                m_wnt = sign.GetComponent<WearNTear>();

                signStates[m_sign] = this;
                
                if (m_piece)
                    pieceStates[m_piece] = this;

                if (m_wnt)
                    wntStates[m_wnt] = this;

                UpdateMapPin();
            }

            public void UpdateSignText(string text)
            {
                if (text == m_text)
                    return;

                m_text = text;

                UpdateMapPin();
            }

            public void UpdateMapPin()
            {
                if (m_pin != null && (!IsPinnableSign(m_text) || m_text != m_pin.m_name))
                    RemoveMapPin();

                if (m_pin == null && IsPinnableSign(m_text))
                {
                    PinType pinType = GetIcon(m_text);
                    m_pin = Minimap.instance.m_pins.FirstOrDefault(pin => pin.m_name == m_text && pin.m_type == pinType && pin.m_save && Utils.DistanceXZ(m_sign.transform.position, pin.m_pos) < 1f);
                    if (m_pin != null)
                        return;

                    m_pin = Minimap.instance.AddPin(m_sign.transform.position, pinType, m_text, save: true, isChecked: checkedList.Any(x => m_text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0));

                    LogInfo($"Added map pin from sign: \"{m_pin.m_name}\" {m_pin.m_icon?.name} {m_pin.m_pos}");

                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "$msg_pin_added: " + m_text, 0, m_pin.m_icon);
                }
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

            public static void UpdatePinState(Sign sign)
            {
                string text = sign.GetText().RemoveRichTextTags();
                if (signStates.TryGetValue(sign, out SignState signState))
                    signState.UpdateSignText(text);
                else
                    new SignState(sign, text);
            }
        }
    }
}
