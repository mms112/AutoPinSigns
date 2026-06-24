using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace AutoPinSigns
{
#nullable enable

    internal static class CustomConfigs
    {
        internal sealed class ConfigurationManagerAttributes
        {
            public Action<ConfigEntryBase>? CustomDrawer;
        }

        private static Type? configManagerStyles;
        private static FieldInfo? fontSizeField;

        internal static void Awake()
        {
            Assembly? configurationManagerAssembly = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; ++i)
            {
                if (assemblies[i].GetName().Name == "ConfigurationManager")
                {
                    configurationManagerAssembly = assemblies[i];
                    break;
                }
            }

            configManagerStyles = configurationManagerAssembly?.GetType("ConfigurationManager.ConfigurationManagerStyles");
            fontSizeField = configManagerStyles == null ? null : AccessTools.Field(configManagerStyles, "fontSize");
        }

        internal static Action<ConfigEntryBase> DrawSeparatedStrings(string separator)
        {
            return configEntry =>
            {
                bool locked = IsReadOnly(configEntry);
                string value = configEntry.BoxedValue as string ?? string.Empty;
                string[] values = value.Split(new[] { separator }, StringSplitOptions.None);
                string[] updatedValues = new string[values.Length + 1];
                int updatedCount = 0;
                bool changed = false;

                GUIStyle textStyle = GetStyle(GUI.skin.textArea);
                GUIStyle buttonStyle = GetStyle(GUI.skin.button);

                GUILayout.BeginVertical();
                for (int i = 0; i < values.Length; ++i)
                {
                    GUILayout.BeginHorizontal();

                    string original = values[i];
                    string edited = GUILayout.TextField(original, textStyle, GUILayout.ExpandWidth(true));
                    if (!locked && edited != original)
                        changed = true;

                    bool remove = GUILayout.Button("x", buttonStyle, GUILayout.Width(21f)) && !locked;
                    if (remove)
                    {
                        changed = true;
                    }
                    else
                    {
                        updatedValues[updatedCount++] = edited;
                    }

                    if (GUILayout.Button("+", buttonStyle, GUILayout.Width(21f)) && !locked)
                    {
                        changed = true;
                        updatedValues[updatedCount++] = string.Empty;
                    }

                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();

                if (!changed)
                    return;

                configEntry.BoxedValue = string.Join(separator, updatedValues, 0, updatedCount);
            };
        }

        private static bool IsReadOnly(ConfigEntryBase configEntry)
        {
            object[] tags = configEntry.Description.Tags;
            for (int i = 0; i < tags.Length; ++i)
            {
                object tag = tags[i];
                Type type = tag.GetType();
                if (type.Name != nameof(ConfigurationManagerAttributes))
                    continue;

                FieldInfo? readOnlyField = type.GetField("ReadOnly");
                if (readOnlyField?.GetValue(tag) is bool readOnly)
                    return readOnly;
            }

            return false;
        }

        private static GUIStyle GetStyle(GUIStyle source)
        {
            if (fontSizeField?.GetValue(null) is not int fontSize || source.fontSize == fontSize)
                return source;

            return new GUIStyle(source) { fontSize = fontSize };
        }
    }
}
