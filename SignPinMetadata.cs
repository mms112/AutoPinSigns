using static AutoPinSigns.AutoPinSigns;
using static Minimap;

namespace AutoPinSigns
{
    internal static class SignPinMetadata
    {
        private const int SchemaVersion = 1;

        private static readonly int SchemaKey = (pluginID + ".Pin.Schema").GetStableHashCode();
        private static readonly int IsPinKey = (pluginID + ".Pin.IsPin").GetStableHashCode();
        private static readonly int TypeKey = (pluginID + ".Pin.Type").GetStableHashCode();
        private static readonly int NameKey = (pluginID + ".Pin.Name").GetStableHashCode();
        private static readonly int TextHashKey = (pluginID + ".Pin.TextHash").GetStableHashCode();
        private static readonly int RulesHashKey = (pluginID + ".Pin.RulesHash").GetStableHashCode();
        private static readonly int CheckedKey = (pluginID + ".Pin.Checked").GetStableHashCode();

        internal static bool IsWritingCacheMetadata { get; private set; }

        internal static bool TryGetPinState(
            ZDO zdo,
            string rawText,
            bool allowMetadataWrite,
            out SignPinMatch match,
            out bool isChecked)
        {
            rawText ??= string.Empty;
            isChecked = allowCheckedPinStatus?.Value == true && zdo != null && zdo.GetBool(CheckedKey, false);

            if (zdo == null)
                return SignPinParser.TryParse(rawText, out match);

            int textHash = rawText.GetStableHashCode();
            bool metadataCurrent =
                zdo.GetInt(SchemaKey, 0) == SchemaVersion &&
                zdo.GetInt(TextHashKey, 0) == textHash &&
                zdo.GetInt(RulesHashKey, 0) == SignPinParser.RulesSignature;

            if (metadataCurrent)
            {
                if (!zdo.GetBool(IsPinKey, false))
                {
                    match = default;
                    return false;
                }

                PinType type = (PinType)zdo.GetInt(TypeKey, (int)PinType.None);
                string name = zdo.GetString(NameKey, string.Empty);
                if (IsUserPinType(type) && !string.IsNullOrWhiteSpace(name))
                {
                    match = new SignPinMatch(type, name);
                    return true;
                }
            }

            bool hasMatch = SignPinParser.TryParse(rawText, out match);
            if (allowMetadataWrite)
                WriteMetadata(zdo, textHash, hasMatch, match);

            return hasMatch;
        }

        internal static bool CanWriteMetadata(Sign sign) =>
            sign &&
            sign.m_nview &&
            sign.m_nview.IsValid() &&
            ((ZNet.instance && ZNet.instance.IsServer()) || sign.m_nview.IsOwner());

        internal static bool SetChecked(Sign sign, bool value)
        {
            if (!sign || !sign.m_nview || !sign.m_nview.IsValid())
                return false;

            if (!sign.m_nview.IsOwner())
                sign.m_nview.ClaimOwnership();

            ZDO zdo = sign.m_nview.GetZDO();
            if (zdo == null)
                return false;

            if (zdo.GetBool(CheckedKey, false) != value)
                zdo.Set(CheckedKey, value);

            return true;
        }

        private static void WriteMetadata(ZDO zdo, int textHash, bool hasMatch, SignPinMatch match)
        {
            bool previousWriteState = IsWritingCacheMetadata;
            IsWritingCacheMetadata = true;
            try
            {
                if (hasMatch)
                {
                    SetIntIfChanged(zdo, TypeKey, (int)match.Type);
                    SetStringIfChanged(zdo, NameKey, match.Name);
                }
                else
                {
                    SetIntIfChanged(zdo, TypeKey, (int)PinType.None);
                    SetStringIfChanged(zdo, NameKey, string.Empty);
                }

                SetBoolIfChanged(zdo, IsPinKey, hasMatch);

                // Validation markers are written after the payload so partially replicated updates
                // are never accepted as a current cache entry.
                SetIntIfChanged(zdo, RulesHashKey, SignPinParser.RulesSignature);
                SetIntIfChanged(zdo, TextHashKey, textHash);
                SetIntIfChanged(zdo, SchemaKey, SchemaVersion);
            }
            finally
            {
                IsWritingCacheMetadata = previousWriteState;
            }
        }

        private static void SetIntIfChanged(ZDO zdo, int key, int value)
        {
            if (zdo.GetInt(key, int.MinValue) != value)
                zdo.Set(key, value);
        }

        private static void SetBoolIfChanged(ZDO zdo, int key, bool value)
        {
            if (zdo.GetBool(key, !value) != value)
                zdo.Set(key, value);
        }

        private static void SetStringIfChanged(ZDO zdo, int key, string value)
        {
            value ??= string.Empty;
            if (zdo.GetString(key, string.Empty) != value)
                zdo.Set(key, value);
        }
    }
}
