using System;
using System.Collections.Generic;
using System.Text;
using static AutoPinSigns.AutoPinSigns;
using static Minimap;

namespace AutoPinSigns
{
    internal readonly struct SignPinMatch
    {
        internal readonly PinType Type;
        internal readonly string Name;

        internal SignPinMatch(PinType type, string name)
        {
            Type = type;
            Name = name;
        }
    }

    internal static class SignPinParser
    {
        internal const string AnyPinToken = "anyPin";
        internal static int RulesSignature { get; private set; }

        private sealed class RuleSet
        {
            internal readonly PinType Type;
            internal string[] ListTokens = Array.Empty<string>();
            internal string[] PrefixTokens = Array.Empty<string>();
            internal string[] SuffixTokens = Array.Empty<string>();
            internal HashSet<string> ExactTokens = new(StringComparer.OrdinalIgnoreCase);
            internal bool AnyPrefix;
            internal bool AnySuffix;

            internal RuleSet(PinType type)
            {
                Type = type;
            }
        }

        private static readonly RuleSet[] rules =
        {
            new(PinType.Icon0),
            new(PinType.Icon1),
            new(PinType.Icon2),
            new(PinType.Icon3),
            new(PinType.Icon4)
        };

        internal static void RebuildRules()
        {
            if (configFireList == null)
                return;

            SetRules(rules[0], configFireList.Value, configFirePrefix.Value, configFireSuffix.Value);
            SetRules(rules[1], configBaseList.Value, configBasePrefix.Value, configBaseSuffix.Value);
            SetRules(rules[2], configHammerList.Value, configHammerPrefix.Value, configHammerSuffix.Value);
            SetRules(rules[3], configPinList.Value, configPinPrefix.Value, configPinSuffix.Value);
            SetRules(rules[4], configPortalList.Value, configPortalPrefix.Value, configPortalSuffix.Value);
            RulesSignature = CalculateRulesSignature();
        }

        internal static bool TryParse(string rawText, out SignPinMatch match)
        {
            match = default;
            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            string text = stripHTMLTags.Value ? StripRichTextTags(rawText) : rawText;
            text = text.Trim();
            if (text.Length == 0)
                return false;

            // A deliberately configured full-text value is the strongest rule.
            if (useStringsList.Value && TryMatchExact(text, out match))
                return true;

            if (useStringsPrefix.Value)
            {
                if (TryMatchAffix(text, prefix: true, out match, out bool matchedEmptyPrefix))
                    return true;
                if (matchedEmptyPrefix)
                    return false;
            }

            if (useStringsSuffix.Value)
            {
                if (TryMatchAffix(text, prefix: false, out match, out bool matchedEmptySuffix))
                    return true;
                if (matchedEmptySuffix)
                    return false;
            }

            if (useStringsList.Value && allowSubstrings.Value && TryMatchSubstring(text, out match))
                return true;

            if (useStringsPrefix.Value && TryGetAnyPinType(prefix: true, out PinType anyPrefix))
            {
                match = new SignPinMatch(anyPrefix, text);
                return true;
            }

            if (useStringsSuffix.Value && TryGetAnyPinType(prefix: false, out PinType anySuffix))
            {
                match = new SignPinMatch(anySuffix, text);
                return true;
            }

            return false;
        }

        private static bool TryMatchExact(string text, out SignPinMatch match)
        {
            for (int ruleIndex = 0; ruleIndex < rules.Length; ++ruleIndex)
            {
                RuleSet rule = rules[ruleIndex];
                if (!rule.ExactTokens.Contains(text))
                    continue;

                match = new SignPinMatch(rule.Type, text);
                return true;
            }

            match = default;
            return false;
        }

        private static bool TryMatchAffix(string text, bool prefix, out SignPinMatch match, out bool matchedWithoutName)
        {
            RuleSet bestRule = null;
            string bestToken = null;

            for (int ruleIndex = 0; ruleIndex < rules.Length; ++ruleIndex)
            {
                RuleSet rule = rules[ruleIndex];
                string[] tokens = prefix ? rule.PrefixTokens : rule.SuffixTokens;
                for (int tokenIndex = 0; tokenIndex < tokens.Length; ++tokenIndex)
                {
                    string token = tokens[tokenIndex];
                    bool matches = prefix
                        ? text.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                        : text.EndsWith(token, StringComparison.OrdinalIgnoreCase);

                    if (!matches || (bestToken != null && token.Length <= bestToken.Length))
                        continue;

                    bestRule = rule;
                    bestToken = token;
                }
            }

            if (bestToken == null)
            {
                match = default;
                matchedWithoutName = false;
                return false;
            }

            string name = prefix
                ? text.Substring(bestToken.Length).Trim()
                : text.Substring(0, text.Length - bestToken.Length).Trim();

            if (name.Length == 0)
            {
                match = default;
                matchedWithoutName = true;
                return false;
            }

            match = new SignPinMatch(bestRule.Type, name);
            matchedWithoutName = false;
            return true;
        }

        private static bool TryMatchSubstring(string text, out SignPinMatch match)
        {
            RuleSet bestRule = null;
            string bestToken = null;

            for (int ruleIndex = 0; ruleIndex < rules.Length; ++ruleIndex)
            {
                RuleSet rule = rules[ruleIndex];
                string[] tokens = rule.ListTokens;
                for (int tokenIndex = 0; tokenIndex < tokens.Length; ++tokenIndex)
                {
                    string token = tokens[tokenIndex];
                    if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0 ||
                        (bestToken != null && token.Length <= bestToken.Length))
                        continue;

                    bestRule = rule;
                    bestToken = token;
                }
            }

            if (bestToken == null)
            {
                match = default;
                return false;
            }

            match = new SignPinMatch(bestRule.Type, text);
            return true;
        }

        private static bool TryGetAnyPinType(bool prefix, out PinType type)
        {
            for (int ruleIndex = 0; ruleIndex < rules.Length; ++ruleIndex)
            {
                RuleSet rule = rules[ruleIndex];
                if (prefix ? rule.AnyPrefix : rule.AnySuffix)
                {
                    type = rule.Type;
                    return true;
                }
            }

            type = PinType.None;
            return false;
        }


        private static int CalculateRulesSignature()
        {
            unchecked
            {
                int hash = 17;
                AddHash(ref hash, useStringsList.Value);
                AddHash(ref hash, useStringsPrefix.Value);
                AddHash(ref hash, useStringsSuffix.Value);
                AddHash(ref hash, allowSubstrings.Value);
                AddHash(ref hash, stripHTMLTags.Value);

                AddHash(ref hash, configFireList.Value);
                AddHash(ref hash, configBaseList.Value);
                AddHash(ref hash, configHammerList.Value);
                AddHash(ref hash, configPinList.Value);
                AddHash(ref hash, configPortalList.Value);

                AddHash(ref hash, configFirePrefix.Value);
                AddHash(ref hash, configBasePrefix.Value);
                AddHash(ref hash, configHammerPrefix.Value);
                AddHash(ref hash, configPinPrefix.Value);
                AddHash(ref hash, configPortalPrefix.Value);

                AddHash(ref hash, configFireSuffix.Value);
                AddHash(ref hash, configBaseSuffix.Value);
                AddHash(ref hash, configHammerSuffix.Value);
                AddHash(ref hash, configPinSuffix.Value);
                AddHash(ref hash, configPortalSuffix.Value);
                return hash;
            }
        }

        private static void AddHash(ref int hash, bool value) => hash = hash * 31 + (value ? 1 : 0);

        private static void AddHash(ref int hash, string value) =>
            hash = hash * 31 + (value ?? string.Empty).GetStableHashCode();

        private static void SetRules(RuleSet rule, string list, string prefixes, string suffixes)
        {
            rule.ListTokens = ParseTokens(list, allowAnyToken: false, out _);
            rule.PrefixTokens = ParseTokens(prefixes, allowAnyToken: true, out rule.AnyPrefix);
            rule.SuffixTokens = ParseTokens(suffixes, allowAnyToken: true, out rule.AnySuffix);
            rule.ExactTokens = new HashSet<string>(rule.ListTokens, StringComparer.OrdinalIgnoreCase);
        }

        private static string[] ParseTokens(string value, bool allowAnyToken, out bool hasAnyToken)
        {
            hasAnyToken = false;
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            string[] split = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> tokens = new(split.Length);
            HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < split.Length; ++i)
            {
                string token = split[i].Trim();
                if (token.Length == 0)
                    continue;

                if (allowAnyToken && token.Equals(AnyPinToken, StringComparison.OrdinalIgnoreCase))
                {
                    hasAnyToken = true;
                    continue;
                }

                if (unique.Add(token))
                    tokens.Add(token);
            }

            // Longer values are checked first within each type; cross-type selection also prefers length.
            tokens.Sort((left, right) =>
            {
                int lengthComparison = right.Length.CompareTo(left.Length);
                return lengthComparison != 0 ? lengthComparison : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            return tokens.ToArray();
        }

        private static string StripRichTextTags(string text)
        {
            int firstTag = text.IndexOf('<');
            if (firstTag < 0)
                return text;

            StringBuilder builder = null;
            int copyStart = 0;

            for (int index = firstTag; index < text.Length; ++index)
            {
                if (text[index] != '<')
                    continue;

                int tagEnd = text.IndexOf('>', index + 1);
                if (tagEnd < 0 || !IsLikelyRichTextTag(text, index + 1, tagEnd))
                    continue;

                builder ??= new StringBuilder(text.Length);
                if (index > copyStart)
                    builder.Append(text, copyStart, index - copyStart);

                index = tagEnd;
                copyStart = tagEnd + 1;
            }

            if (builder == null)
                return text;

            if (copyStart < text.Length)
                builder.Append(text, copyStart, text.Length - copyStart);

            return builder.ToString();
        }

        private static bool IsLikelyRichTextTag(string text, int start, int end)
        {
            if (start >= end)
                return false;

            char first = text[start];
            if (first == '/')
                return start + 1 < end && char.IsLetter(text[start + 1]);

            return first == '#' || char.IsLetter(first);
        }
    }
}
