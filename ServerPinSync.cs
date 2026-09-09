using HarmonyLib;
using Splatform;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using static AutoPinSigns.AutoPinSigns;
using static Minimap;

namespace AutoPinSigns
{
    internal static class ServerPinSync
    {
        private const int ProtocolVersion = 2;
        private const int MaximumReceivedPins = 100000;
        private const float RebuildDelaySeconds = 0.35f;
        private const float InitialRequestDelaySeconds = 0.5f;
        private const float RequestRetrySeconds = 5f;
        private const float MinimumRequestIntervalSeconds = 1f;
        private const int MaximumAutomaticRequestAttempts = 6;
        private const int MaximumBackgroundProbeAttempts = 2;
        private const float ProjectionAuditIntervalSeconds = 1f;
        private const float ProjectionLogIntervalSeconds = 5f;

        private const string RequestRpc = pluginID + ".ServerPins.Request";
        private const string ResponseRpc = pluginID + ".ServerPins.Response";

        private readonly struct ServerPin : IEquatable<ServerPin>
        {
            internal readonly Vector3 Position;
            internal readonly PinType Type;
            internal readonly string Name;
            internal readonly bool Checked;
            internal readonly long Creator;
            internal readonly string Author;

            internal ServerPin(Vector3 position, PinType type, string name, bool isChecked, long creator, string author)
            {
                Position = position;
                Type = type;
                Name = name ?? string.Empty;
                Checked = isChecked;
                Creator = creator;
                Author = author ?? string.Empty;
            }

            public bool Equals(ServerPin other) =>
                Position == other.Position &&
                Type == other.Type &&
                Name == other.Name &&
                Checked == other.Checked &&
                Creator == other.Creator &&
                Author == other.Author;
        }

        private readonly struct ServerPinCandidate
        {
            internal readonly string ZdoId;
            internal readonly ServerPin Pin;

            internal ServerPinCandidate(string zdoId, ServerPin pin)
            {
                ZdoId = zdoId ?? string.Empty;
                Pin = pin;
            }
        }

        private readonly struct PinIdentity : IEquatable<PinIdentity>
        {
            private readonly PinType type;
            private readonly string name;

            internal PinIdentity(PinType type, string name)
            {
                this.type = type;
                this.name = name ?? string.Empty;
            }

            public bool Equals(PinIdentity other) => type == other.type && name == other.name;

            public override bool Equals(object obj) => obj is PinIdentity other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)type * 397) ^ name.GetHashCode();
                }
            }
        }

        private sealed class PinDataReferenceComparer : IEqualityComparer<PinData>
        {
            internal static readonly PinDataReferenceComparer Instance = new();

            public bool Equals(PinData left, PinData right) => ReferenceEquals(left, right);

            public int GetHashCode(PinData value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class MapDataPinSwapState
        {
            internal readonly List<PinData> RemovedAuthoritativePins = new();
            internal readonly List<PinData> AddedClientPins = new();
        }

        private sealed class ServerPinComparer : IComparer<ServerPin>
        {
            internal static readonly ServerPinComparer Instance = new();

            public int Compare(ServerPin left, ServerPin right)
            {
                int result = left.Position.x.CompareTo(right.Position.x);
                if (result != 0) return result;
                result = left.Position.z.CompareTo(right.Position.z);
                if (result != 0) return result;
                result = left.Position.y.CompareTo(right.Position.y);
                if (result != 0) return result;
                result = ((int)left.Type).CompareTo((int)right.Type);
                if (result != 0) return result;
                result = string.CompareOrdinal(left.Name, right.Name);
                if (result != 0) return result;
                result = left.Checked.CompareTo(right.Checked);
                if (result != 0) return result;
                result = left.Creator.CompareTo(right.Creator);
                if (result != 0) return result;
                return string.CompareOrdinal(left.Author, right.Author);
            }
        }


        private sealed class ServerPinCandidateComparer : IComparer<ServerPinCandidate>
        {
            internal static readonly ServerPinCandidateComparer Instance = new();

            public int Compare(ServerPinCandidate left, ServerPinCandidate right) =>
                string.CompareOrdinal(left.ZdoId, right.ZdoId);
        }

        private static readonly HashSet<int> signPrefabHashes = new() { "sign".GetStableHashCode() };
        private static readonly HashSet<ZDO> signZdos = new();
        private static readonly List<ZDO> staleZdos = new();

        private static readonly List<ServerPin> authoritativePins = new();
        private static readonly List<ServerPin> rebuiltPins = new();
        private static readonly List<ServerPinCandidate> candidatePins = new();
        private static readonly Dictionary<PinIdentity, List<ServerPin>> mergedPinGroups = new();
        private static readonly List<ServerPin> receivedPins = new();
        private static readonly List<PinData> appliedPins = new();
        private static readonly HashSet<PinData> appliedPinSet = new(PinDataReferenceComparer.Instance);
        private static readonly List<PinData> hiddenClientPins = new();
        private static readonly HashSet<PinData> hiddenClientPinSet = new(PinDataReferenceComparer.Instance);
        private static readonly Dictionary<PinData, int> hiddenClientPinIndices = new(PinDataReferenceComparer.Instance);
        private static readonly Dictionary<long, float> nextAllowedRequestByPeer = new();
        private static readonly MethodInfo listContainsIdMethod = AccessTools.DeclaredMethod(typeof(ZNet), "ListContainsId");
        private static readonly object[] listContainsIdArguments = new object[2];

        private static ZRoutedRpc registeredRpcInstance;
        private static bool serverListDirty;
        private static bool serverModeBroadcastPending;
        private static float rebuildAt;
        private static long serverRevision;

        private static bool clientHasSnapshot;
        private static bool clientAuthorityConfirmed;
        private static AuthoritativePinTypes clientAuthoritativeTypes = AuthoritativePinTypes.None;
        private static long clientRevision = -1;
        private static float nextRequestAt;
        private static int clientRequestAttempts;
        private static bool clientRequestLimitLogged;
        private static bool modeStateInitialized;
        private static bool lastAuthoritativeMode;

        private static Minimap projectionMinimap;
        private static Minimap hiddenClientPinsMinimap;
        private static bool projectionDirty;
        private static bool isReconcilingProjection;
        private static bool isSwappingMapDataPins;
        private static int lastObservedPinCount = -1;
        private static float nextProjectionAuditAt;
        private static float nextProjectionLogAt;
        private static bool initialProjectionCompleted;

        private static bool HasConfirmedAuthority =>
            ZNet.instance && (ZNet.instance.IsServer() ? IsAuthoritativeMode : IsEnabled && clientAuthorityConfirmed);

        internal static bool HasActiveAuthority => HasConfirmedAuthority;

        private static AuthoritativePinTypes ControlledPinTypes =>
            ZNet.instance && ZNet.instance.IsServer()
                ? GetConfiguredAuthoritativePinTypes()
                : clientAuthoritativeTypes;

        internal static bool ControlsPinType(PinType pinType) =>
            HasConfirmedAuthority && IsPinTypeEnabled(ControlledPinTypes, pinType);

        internal static void Tick()
        {
            if (!ZNet.instance || ZRoutedRpc.instance == null)
                return;

            RegisterRpcs();

            bool authoritativeMode = IsAuthoritativeMode;
            if (!modeStateInitialized || lastAuthoritativeMode != authoritativeMode)
            {
                modeStateInitialized = true;
                lastAuthoritativeMode = authoritativeMode;
                ApplyModeState();
            }

            if (ZNet.instance.IsServer())
            {
                if (IsAuthoritativeMode && serverListDirty && Time.realtimeSinceStartup >= rebuildAt)
                    RebuildServerList(broadcastIfChanged: true);
                else if (!IsAuthoritativeMode && serverModeBroadcastPending && BroadcastSnapshot())
                    serverModeBroadcastPending = false;

                MaintainAuthoritativeProjection();
                return;
            }

            MaintainAuthoritativeProjection();

            if (!IsEnabled)
                return;

            // Probe the server independently of config synchronization order. The RPC response
            // is the authority for whether this particular server controls user pins.
            int maximumAttempts = IsAuthoritativeMode ? MaximumAutomaticRequestAttempts : MaximumBackgroundProbeAttempts;
            if (!clientHasSnapshot && clientRequestAttempts < maximumAttempts && Time.realtimeSinceStartup >= nextRequestAt)
            {
                RequestServerPins();
            }
            else if (!clientHasSnapshot && clientRequestAttempts >= maximumAttempts && !clientRequestLimitLogged)
            {
                clientRequestLimitLogged = true;
                if (IsAuthoritativeMode)
                    LogWarning("Authoritative pin synchronization received no server response and stopped automatic retries. Use 'autopinsigns resync' to retry.");
            }
        }

        internal static void OnModeChanged()
        {
            modeStateInitialized = true;
            lastAuthoritativeMode = IsAuthoritativeMode;
            ApplyModeState();
        }

        internal static void OnAuthoritativeSettingsChanged()
        {
            if (!ZNet.instance)
                return;

            if (ZNet.instance.IsServer())
            {
                RemoveAppliedPins();
                HideControlledClientPins(Minimap.instance);
                LocalSignPins.RefreshAll();
                serverModeBroadcastPending = true;
                MarkProjectionDirty();
                MarkDirty(immediate: true);
                return;
            }

            if (clientAuthorityConfirmed)
            {
                clientHasSnapshot = false;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                nextRequestAt = Time.realtimeSinceStartup;
            }
        }

        private static void ApplyModeState()
        {
            if (ZNet.instance && ZNet.instance.IsServer())
            {
                if (IsAuthoritativeMode)
                {
                    HideControlledClientPins(Minimap.instance);
                    LocalSignPins.RefreshAll();
                    serverModeBroadcastPending = true;
                    initialProjectionCompleted = false;
                    MarkProjectionDirty();
                    DiscoverSignPrefabs();
                    RebuildSignIndex();
                    MarkDirty(immediate: true);
                }
                else
                {
                    RemoveAppliedPins();
                    ResetProjectionTracking();
                    initialProjectionCompleted = false;
                    serverModeBroadcastPending = true;
                    if (BroadcastSnapshot())
                        serverModeBroadcastPending = false;
                    LocalSignPins.RefreshAll();
                }
                return;
            }

            if (!IsEnabled)
            {
                RemoveAppliedPins();
                receivedPins.Clear();
                clientHasSnapshot = false;
                clientAuthorityConfirmed = false;
                clientAuthoritativeTypes = AuthoritativePinTypes.None;
                clientRevision = -1;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                initialProjectionCompleted = false;
                ResetProjectionTracking();
                LocalSignPins.RefreshAll();
                return;
            }

            // The server RPC, not the timing of the synchronized config callback, confirms
            // whether authoritative mode is active for the current connection. If the synced
            // config expects authority but an earlier probe saw local mode, request again.
            if (!clientHasSnapshot || (IsAuthoritativeMode && !clientAuthorityConfirmed))
            {
                clientHasSnapshot = false;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                nextRequestAt = Time.realtimeSinceStartup + InitialRequestDelaySeconds;
            }

            if (clientAuthorityConfirmed)
            {
                MarkProjectionDirty();
                MaintainAuthoritativeProjection(forceAudit: true);
            }

            LocalSignPins.RefreshAll();
        }

        internal static void MarkDirty(bool immediate = false)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
                return;

            serverListDirty = true;
            float targetTime = Time.realtimeSinceStartup + (immediate ? 0f : RebuildDelaySeconds);
            if (rebuildAt <= Time.realtimeSinceStartup || targetTime < rebuildAt)
                rebuildAt = targetTime;
        }

        internal static void RegisterLoadedSign(Sign sign)
        {
            if (!sign || !sign.m_nview || !sign.m_nview.IsValid())
                return;

            ZDO zdo = sign.m_nview.GetZDO();
            if (zdo == null)
                return;

            bool newPrefab = signPrefabHashes.Add(zdo.GetPrefab());
            if (ZNet.instance && ZNet.instance.IsServer())
            {
                signZdos.Add(zdo);
                if (newPrefab)
                    RebuildSignIndex();
                MarkDirty();
            }
        }

        internal static string ForceResync()
        {
            if (!IsEnabled)
                return "Auto Pin Signs is disabled.";

            if (ZNet.instance && ZNet.instance.IsServer() && !IsAuthoritativeMode)
            {
                LocalSignPins.RefreshAll();
                return "Auto Pin Signs local sign pins refreshed.";
            }

            if (ZNet.instance && ZNet.instance.IsServer())
            {
                DiscoverSignPrefabs();
                RebuildSignIndex();
                MarkDirty(immediate: true);
                return "Auto Pin Signs authoritative rebuild queued.";
            }

            clientHasSnapshot = false;
            clientRequestAttempts = 0;
            clientRequestLimitLogged = false;
            nextRequestAt = Time.realtimeSinceStartup;
            return "Auto Pin Signs authoritative snapshot requested.";
        }

        internal static string GetStatusText()
        {
            if (!IsEnabled)
                return "Auto Pin Signs: disabled.";

            if (!ZNet.instance)
                return IsAuthoritativeMode
                    ? "Auto Pin Signs: authoritative mode configured, no active network session."
                    : "Auto Pin Signs: local sign discovery mode, no active network session.";

            if (ZNet.instance.IsServer())
            {
                if (!IsAuthoritativeMode)
                    return "Auto Pin Signs: local sign discovery mode.";

                return $"Auto Pin Signs: server authoritative mode, revision {serverRevision}, controlled types {ControlledPinTypes}, {authoritativePins.Count} pin(s), {appliedPins.Count} projected pin(s), {hiddenClientPins.Count} hidden client pin(s), {signZdos.Count} indexed sign ZDO(s).";
            }

            string confirmation = clientAuthorityConfirmed ? "ready" : clientHasSnapshot ? "server mode inactive" : "pending";
            return $"Auto Pin Signs: server authoritative probe {confirmation}, revision {clientRevision}, controlled types {clientAuthoritativeTypes}, {receivedPins.Count} received pin(s), {appliedPins.Count} projected pin(s), {hiddenClientPins.Count} hidden client pin(s), request attempts {clientRequestAttempts}/{MaximumAutomaticRequestAttempts}.";
        }

        internal static void ResetSession()
        {
            RemoveAppliedPins();
            signZdos.Clear();
            staleZdos.Clear();
            authoritativePins.Clear();
            rebuiltPins.Clear();
            candidatePins.Clear();
            mergedPinGroups.Clear();
            receivedPins.Clear();
            nextAllowedRequestByPeer.Clear();
            serverListDirty = false;
            serverModeBroadcastPending = false;
            rebuildAt = 0f;
            serverRevision = 0;
            clientHasSnapshot = false;
            clientAuthorityConfirmed = false;
            clientAuthoritativeTypes = AuthoritativePinTypes.None;
            clientRevision = -1;
            nextRequestAt = 0f;
            clientRequestAttempts = 0;
            clientRequestLimitLogged = false;
            modeStateInitialized = false;
            lastAuthoritativeMode = false;
            initialProjectionCompleted = false;
            ResetProjectionTracking();
            hiddenClientPins.Clear();
            hiddenClientPinSet.Clear();
            hiddenClientPinIndices.Clear();
            hiddenClientPinsMinimap = null;
            isSwappingMapDataPins = false;
        }

        private static void RegisterRpcs()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(registeredRpcInstance, rpc))
                return;

            rpc.Register<ZPackage>(RequestRpc, RPC_RequestServerPins);
            rpc.Register<ZPackage>(ResponseRpc, RPC_ReceiveServerPins);
            registeredRpcInstance = rpc;
        }

        private static void RequestServerPins()
        {
            ++clientRequestAttempts;
            nextRequestAt = Time.realtimeSinceStartup + RequestRetrySeconds;
            if (ZRoutedRpc.instance == null || !ZNet.instance || ZNet.instance.IsServer())
                return;

            ZPackage package = new();
            package.Write(ProtocolVersion);
            package.Write(clientRevision);
            package.Write((int)clientAuthoritativeTypes);

            long serverPeerId = ZRoutedRpc.instance.GetServerPeerID();
            ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RequestRpc, package);
            LogInfo($"Requested authoritative server pins; local revision {clientRevision}.");
        }

        private static void RPC_RequestServerPins(long sender, ZPackage package)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || package == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (nextAllowedRequestByPeer.TryGetValue(sender, out float allowedAt) && now < allowedAt)
                return;
            nextAllowedRequestByPeer[sender] = now + MinimumRequestIntervalSeconds;

            try
            {
                int protocol = package.ReadInt();
                long requestedRevision = package.ReadLong();
                AuthoritativePinTypes requestedTypes = (AuthoritativePinTypes)package.ReadInt() & AuthoritativePinTypes.All;
                if (protocol != ProtocolVersion)
                {
                    LogWarning($"Ignored Auto Pin Signs request using unsupported protocol {protocol} from peer {sender}.");
                    return;
                }

                bool changedAndBroadcast = IsAuthoritativeMode && serverListDirty && RebuildServerList(broadcastIfChanged: true);
                if (!changedAndBroadcast)
                    SendSnapshot(sender, requestedRevision, requestedTypes);
            }
            catch (Exception exception)
            {
                LogWarning($"Failed to read Auto Pin Signs request from peer {sender}: {exception.Message}");
            }
        }

        private static void SendSnapshot(long target, long requestedRevision, AuthoritativePinTypes requestedTypes)
        {
            ZPackage package = CreateSnapshotPackage(requestedRevision, requestedTypes);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, ResponseRpc, package);
        }

        private static bool BroadcastSnapshot()
        {
            if (ZRoutedRpc.instance == null || !ZNet.instance || !ZNet.instance.IsServer())
                return false;

            ZPackage package = CreateSnapshotPackage(requestedRevision: -1, requestedTypes: AuthoritativePinTypes.None);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ResponseRpc, package);
            return true;
        }

        private static ZPackage CreateSnapshotPackage(long requestedRevision, AuthoritativePinTypes requestedTypes)
        {
            ZPackage package = new();
            AuthoritativePinTypes currentTypes = IsAuthoritativeMode
                ? GetConfiguredAuthoritativePinTypes()
                : AuthoritativePinTypes.None;

            package.Write(ProtocolVersion);
            package.Write(IsAuthoritativeMode);
            package.Write((int)currentTypes);
            package.Write(serverRevision);

            bool includePins = IsAuthoritativeMode && (requestedRevision != serverRevision || requestedTypes != currentTypes);
            package.Write(includePins);
            if (!includePins)
                return package;

            package.Write(authoritativePins.Count);
            for (int i = 0; i < authoritativePins.Count; ++i)
            {
                ServerPin pin = authoritativePins[i];
                package.Write(pin.Position);
                package.Write((int)pin.Type);
                package.Write(pin.Name);
                package.Write(pin.Checked);
                package.Write(pin.Creator);
                package.Write(pin.Author);
            }

            return package;
        }

        private static void RPC_ReceiveServerPins(long sender, ZPackage package)
        {
            if (!ZNet.instance || ZNet.instance.IsServer() || package == null || ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID())
                return;

            try
            {
                int protocol = package.ReadInt();
                if (protocol != ProtocolVersion)
                {
                    LogWarning($"Ignored authoritative pin snapshot using unsupported protocol {protocol}.");
                    return;
                }

                bool enabled = package.ReadBool();
                AuthoritativePinTypes authoritativeTypes = (AuthoritativePinTypes)package.ReadInt() & AuthoritativePinTypes.All;
                long revision = package.ReadLong();
                bool includesPins = package.ReadBool();

                if (!enabled)
                {
                    RemoveAppliedPins();
                    receivedPins.Clear();
                    clientHasSnapshot = true;
                    clientAuthorityConfirmed = false;
                    clientAuthoritativeTypes = AuthoritativePinTypes.None;
                    clientRevision = revision;
                    initialProjectionCompleted = false;
                    ResetProjectionTracking();
                    clientRequestAttempts = 0;
                    clientRequestLimitLogged = false;
                    LocalSignPins.RefreshAll();
                    return;
                }

                bool controlledTypesChanged = clientAuthorityConfirmed && clientAuthoritativeTypes != (authoritativeTypes & AuthoritativePinTypes.All);
                if (controlledTypesChanged)
                {
                    RemoveAppliedPins();
                    initialProjectionCompleted = false;
                }

                if (includesPins)
                {
                    int count = package.ReadInt();
                    if (count < 0 || count > MaximumReceivedPins)
                    {
                        LogWarning($"Rejected authoritative pin snapshot with invalid count {count}.");
                        clientHasSnapshot = false;
                        nextRequestAt = Time.realtimeSinceStartup + RequestRetrySeconds;
                        return;
                    }

                    receivedPins.Clear();
                    for (int i = 0; i < count; ++i)
                    {
                        Vector3 position = package.ReadVector3();
                        PinType type = (PinType)package.ReadInt();
                        string name = package.ReadString();
                        bool isChecked = package.ReadBool();
                        long creator = package.ReadLong();
                        string author = package.ReadString();

                        if (IsPinTypeEnabled(authoritativeTypes, type))
                            receivedPins.Add(new ServerPin(position, type, name, isChecked, creator, author));
                    }
                }

                bool newlyConfirmed = !clientAuthorityConfirmed;
                clientRevision = revision;
                clientAuthoritativeTypes = authoritativeTypes & AuthoritativePinTypes.All;
                clientHasSnapshot = true;
                clientAuthorityConfirmed = true;
                if (newlyConfirmed)
                    initialProjectionCompleted = false;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                MarkProjectionDirty();
                MaintainAuthoritativeProjection(forceAudit: true);
                LocalSignPins.RefreshAll();

                LogInfo($"Received authoritative server pins revision {clientRevision}; {receivedPins.Count} pin(s), controlled types: {clientAuthoritativeTypes}.");
            }
            catch (Exception exception)
            {
                clientHasSnapshot = false;
                nextRequestAt = Time.realtimeSinceStartup + RequestRetrySeconds;
                LogWarning($"Failed to read authoritative pin snapshot: {exception.Message}");
            }
        }

        private static List<ServerPin> GetProjectionSource() =>
            ZNet.instance && ZNet.instance.IsServer() ? authoritativePins : receivedPins;

        private static void MarkProjectionDirty()
        {
            projectionDirty = true;
            nextProjectionAuditAt = 0f;
        }

        private static void ResetProjectionTracking()
        {
            projectionMinimap = null;
            projectionDirty = false;
            isReconcilingProjection = false;
            lastObservedPinCount = -1;
            nextProjectionAuditAt = 0f;
            nextProjectionLogAt = 0f;
            appliedPins.Clear();
            appliedPinSet.Clear();
        }

        private static void MaintainAuthoritativeProjection(bool forceAudit = false)
        {
            if (!HasConfirmedAuthority || !Minimap.instance || isReconcilingProjection || isSwappingMapDataPins)
                return;

            Minimap minimap = Minimap.instance;
            if (!ReferenceEquals(projectionMinimap, minimap))
            {
                projectionMinimap = minimap;
                appliedPins.Clear();
                appliedPinSet.Clear();
                if (hiddenClientPinsMinimap && !ReferenceEquals(hiddenClientPinsMinimap, minimap))
                {
                    hiddenClientPins.Clear();
                    hiddenClientPinSet.Clear();
                    hiddenClientPinIndices.Clear();
                    hiddenClientPinsMinimap = null;
                }
                lastObservedPinCount = -1;
                MarkProjectionDirty();
            }

            float now = Time.realtimeSinceStartup;
            int currentCount = minimap.m_pins.Count;
            bool countChanged = currentCount != lastObservedPinCount;
            bool periodicAudit = now >= nextProjectionAuditAt;

            if (projectionDirty && !forceAudit && now < nextProjectionAuditAt)
                return;

            if (!projectionDirty && !countChanged && !forceAudit && !periodicAudit)
                return;

            List<ServerPin> source = GetProjectionSource();
            if (!ProjectionMatches(minimap, source))
                RebuildProjection(minimap, source);
            else
            {
                projectionDirty = false;
                lastObservedPinCount = currentCount;
                nextProjectionAuditAt = now + ProjectionAuditIntervalSeconds;
            }
        }

        private static bool ProjectionMatches(Minimap minimap, List<ServerPin> source)
        {
            if (appliedPins.Count != source.Count || appliedPinSet.Count != source.Count)
                return false;

            int foundAppliedPins = 0;
            List<PinData> mapPins = minimap.m_pins;
            for (int i = 0; i < mapPins.Count; ++i)
            {
                PinData pin = mapPins[i];
                if (pin == null)
                    continue;

                if (!ControlsPinType(pin.m_type))
                    continue;

                // Controlled standard user icon types are a projection of the server snapshot.
                // Any foreign pin using one of those types is stale.
                if (!appliedPinSet.Contains(pin))
                    return false;

                ++foundAppliedPins;
            }

            if (foundAppliedPins != appliedPins.Count)
                return false;

            for (int i = 0; i < source.Count; ++i)
            {
                PinData applied = appliedPins[i];
                ServerPin expected = source[i];
                if (applied == null ||
                    applied.m_save ||
                    applied.m_type != expected.Type ||
                    applied.m_name != expected.Name ||
                    applied.m_checked != expected.Checked ||
                    applied.m_pos != expected.Position)
                {
                    return false;
                }
            }

            return true;
        }

        private static int HideControlledClientPins(Minimap minimap, bool replaceSnapshot = false)
        {
            if (!minimap)
                return 0;

            if (replaceSnapshot || !ReferenceEquals(hiddenClientPinsMinimap, minimap))
            {
                hiddenClientPins.Clear();
                hiddenClientPinSet.Clear();
                hiddenClientPinIndices.Clear();
                hiddenClientPinsMinimap = minimap;
            }

            int hiddenCount = 0;
            List<PinData> mapPins = minimap.m_pins;
            for (int i = 0; i < mapPins.Count; ++i)
            {
                PinData pin = mapPins[i];
                if (pin == null || !pin.m_save || !ControlsPinType(pin.m_type) || appliedPinSet.Contains(pin))
                    continue;

                if (hiddenClientPinSet.Add(pin))
                {
                    hiddenClientPins.Add(pin);
                    hiddenClientPinIndices[pin] = i;
                    ++hiddenCount;
                }
            }

            for (int i = mapPins.Count - 1; i >= 0; --i)
            {
                PinData pin = mapPins[i];
                if (pin == null || !hiddenClientPinSet.Contains(pin))
                    continue;

                minimap.RemovePin(pin);
            }

            return hiddenCount;
        }

        private static void RestoreHiddenClientPins(Minimap minimap)
        {
            if (!minimap || !ReferenceEquals(hiddenClientPinsMinimap, minimap))
                return;

            List<PinData> mapPins = minimap.m_pins;
            for (int i = 0; i < hiddenClientPins.Count; ++i)
            {
                PinData pin = hiddenClientPins[i];
                if (pin != null && !mapPins.Contains(pin))
                {
                    int index = hiddenClientPinIndices.TryGetValue(pin, out int originalIndex)
                        ? Mathf.Clamp(originalIndex, 0, mapPins.Count)
                        : mapPins.Count;
                    mapPins.Insert(index, pin);
                }
            }

            hiddenClientPins.Clear();
            hiddenClientPinSet.Clear();
            hiddenClientPinIndices.Clear();
            hiddenClientPinsMinimap = null;
        }

        private static MapDataPinSwapState SwapAuthoritativePinsForClientPins(Minimap minimap)
        {
            if (!minimap || isSwappingMapDataPins)
                return null;

            if (hiddenClientPinsMinimap && !ReferenceEquals(hiddenClientPinsMinimap, minimap))
            {
                hiddenClientPins.Clear();
                hiddenClientPinSet.Clear();
                hiddenClientPinIndices.Clear();
                hiddenClientPinsMinimap = null;
            }

            MapDataPinSwapState state = null;
            try
            {
                if (HasConfirmedAuthority)
                {
                    isReconcilingProjection = true;
                    try
                    {
                        HideControlledClientPins(minimap);
                    }
                    finally
                    {
                        isReconcilingProjection = false;
                    }
                }

                if (appliedPins.Count == 0 && hiddenClientPins.Count == 0)
                    return null;

                state = new MapDataPinSwapState();
                isSwappingMapDataPins = true;

                List<PinData> mapPins = minimap.m_pins;
                for (int i = mapPins.Count - 1; i >= 0; --i)
                {
                    PinData pin = mapPins[i];
                    if (pin == null || !appliedPinSet.Contains(pin))
                        continue;

                    state.RemovedAuthoritativePins.Insert(0, pin);
                    mapPins.RemoveAt(i);
                }

                for (int i = 0; i < hiddenClientPins.Count; ++i)
                {
                    PinData pin = hiddenClientPins[i];
                    if (pin == null || mapPins.Contains(pin))
                        continue;

                    int index = hiddenClientPinIndices.TryGetValue(pin, out int originalIndex)
                        ? Mathf.Clamp(originalIndex, 0, mapPins.Count)
                        : mapPins.Count;
                    mapPins.Insert(index, pin);
                    state.AddedClientPins.Add(pin);
                }

                return state;
            }
            catch (Exception exception)
            {
                RestoreAuthoritativePinsAfterMapData(minimap, state);
                RestoreHiddenClientPins(minimap);
                MarkProjectionDirty();
                LogWarning($"Failed to prepare client pins for player profile serialization: {exception.Message}");
                return null;
            }
        }

        private static void RestoreAuthoritativePinsAfterMapData(Minimap minimap, MapDataPinSwapState state)
        {
            if (state == null)
                return;

            try
            {
                if (!minimap)
                    return;

                List<PinData> mapPins = minimap.m_pins;
                for (int i = state.AddedClientPins.Count - 1; i >= 0; --i)
                    mapPins.Remove(state.AddedClientPins[i]);

                for (int i = 0; i < state.RemovedAuthoritativePins.Count; ++i)
                {
                    PinData pin = state.RemovedAuthoritativePins[i];
                    if (pin != null && !mapPins.Contains(pin))
                        mapPins.Add(pin);
                }

                lastObservedPinCount = mapPins.Count;
            }
            catch (Exception exception)
            {
                MarkProjectionDirty();
                LogWarning($"Failed to restore authoritative pins after player profile serialization: {exception.Message}");
            }
            finally
            {
                isSwappingMapDataPins = false;
            }
        }

        private static void RebuildProjection(Minimap minimap, List<ServerPin> source)
        {
            isReconcilingProjection = true;
            bool firstProjection = !initialProjectionCompleted;
            bool completed = false;
            int hiddenSavedPins = 0;

            try
            {
                hiddenSavedPins = HideControlledClientPins(minimap);
                List<PinData> mapPins = minimap.m_pins;
                for (int i = mapPins.Count - 1; i >= 0; --i)
                {
                    PinData pin = mapPins[i];
                    if (pin == null || !ControlsPinType(pin.m_type))
                        continue;

                    minimap.RemovePin(pin);
                }

                appliedPins.Clear();
                appliedPinSet.Clear();

                for (int i = 0; i < source.Count; ++i)
                {
                    ServerPin pin = source[i];
                    PinData mapPin = minimap.AddPin(
                        pin.Position,
                        pin.Type,
                        pin.Name,
                        save: false,
                        isChecked: pin.Checked,
                        pin.Creator,
                        ResolveAuthor(pin.Author));

                    if (mapPin == null)
                        continue;

                    appliedPins.Add(mapPin);
                    appliedPinSet.Add(mapPin);
                }

                completed = appliedPins.Count == source.Count;
                if (!completed)
                    LogWarning($"Authoritative pin projection created {appliedPins.Count} of {source.Count} expected pin(s); retrying later.");
            }
            catch (Exception exception)
            {
                LogWarning($"Failed to reconcile authoritative pin projection: {exception.Message}");
            }
            finally
            {
                isReconcilingProjection = false;
                projectionDirty = !completed;
                lastObservedPinCount = minimap.m_pins.Count;
                nextProjectionAuditAt = Time.realtimeSinceStartup + ProjectionAuditIntervalSeconds;
                if (completed)
                    initialProjectionCompleted = true;
            }

            if (completed)
                ReportHiddenSavedPins(hiddenSavedPins, firstProjection);
        }

        private static void ReportHiddenSavedPins(int hiddenCount, bool firstProjection)
        {
            if (hiddenCount <= 0)
                return;

            string message = $"Server authoritative pins temporarily hid {hiddenCount} saved client user pin(s).";
            if (firstProjection)
            {
                string notice = message + " They remain stored in the player profile and return when server authority or the corresponding controlled type is inactive.";
                LogInfo(notice);
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, notice, 0, null);
                nextProjectionLogAt = Time.realtimeSinceStartup + ProjectionLogIntervalSeconds;
                return;
            }

            if (Time.realtimeSinceStartup >= nextProjectionLogAt)
            {
                nextProjectionLogAt = Time.realtimeSinceStartup + ProjectionLogIntervalSeconds;
                LogInfo(message + " The authoritative projection was restored without changing profile data.");
            }
        }

        private static PlatformUserID ResolveAuthor(string authorValue)
        {
            string resolvedAuthor = authorValue;
            if (string.IsNullOrEmpty(resolvedAuthor))
                return PlatformUserID.None;

            if (resolvedAuthor == "host" && !RelationsManager.UpdateAuthorIfHost(resolvedAuthor, ref resolvedAuthor))
                return PlatformUserID.None;

            return string.IsNullOrEmpty(resolvedAuthor) ? PlatformUserID.None : new PlatformUserID(resolvedAuthor);
        }

        private static void RemoveAppliedPins(bool restoreClientPins = true)
        {
            Minimap minimap = Minimap.instance;
            if (minimap)
            {
                isReconcilingProjection = true;
                try
                {
                    for (int i = appliedPins.Count - 1; i >= 0; --i)
                    {
                        PinData pin = appliedPins[i];
                        if (pin != null && minimap.m_pins.Contains(pin))
                            minimap.RemovePin(pin);
                    }
                }
                finally
                {
                    isReconcilingProjection = false;
                }
            }

            appliedPins.Clear();
            appliedPinSet.Clear();
            if (restoreClientPins)
                RestoreHiddenClientPins(minimap);
            lastObservedPinCount = minimap ? minimap.m_pins.Count : -1;
        }

        private static bool RebuildServerList(bool broadcastIfChanged)
        {
            serverListDirty = false;
            rebuiltPins.Clear();
            candidatePins.Clear();
            mergedPinGroups.Clear();
            staleZdos.Clear();

            AuthoritativePinTypes controlledTypes = GetConfiguredAuthoritativePinTypes();
            bool administratorsOnly = serverAuthoritativeAdminsOnly?.Value == true;
            float mergeDistance = Mathf.Max(0f, serverAuthoritativeMergeDistance?.Value ?? 0f);
            int ignoredNonAdminPins = 0;
            int mergedPins = 0;

            foreach (ZDO zdo in signZdos)
            {
                if (zdo == null || !signPrefabHashes.Contains(zdo.GetPrefab()))
                {
                    staleZdos.Add(zdo);
                    continue;
                }

                string rawText = zdo.GetString(ZDOVars.s_text);
                if (!SignPinMetadata.TryGetPinState(zdo, rawText, allowMetadataWrite: true, out SignPinMatch match, out bool isChecked) ||
                    !IsPinTypeEnabled(controlledTypes, match.Type))
                {
                    continue;
                }

                string author = zdo.GetString(ZDOVars.s_author);
                if (administratorsOnly && !IsAdministratorAuthor(author))
                {
                    ++ignoredNonAdminPins;
                    continue;
                }

                ServerPin pin = new(
                    zdo.GetPosition(),
                    match.Type,
                    match.Name,
                    isChecked,
                    zdo.GetLong(ZDOVars.s_creator, 0L),
                    author);
                candidatePins.Add(new ServerPinCandidate(zdo.m_uid.ToString(), pin));
            }

            for (int i = 0; i < staleZdos.Count; ++i)
                signZdos.Remove(staleZdos[i]);
            staleZdos.Clear();

            candidatePins.Sort(ServerPinCandidateComparer.Instance);
            for (int i = 0; i < candidatePins.Count; ++i)
            {
                ServerPin pin = candidatePins[i].Pin;
                if (mergeDistance > 0f && IsMergedDuplicate(pin, mergeDistance))
                {
                    ++mergedPins;
                    continue;
                }

                rebuiltPins.Add(pin);
                if (mergeDistance > 0f)
                {
                    PinIdentity identity = new(pin.Type, pin.Name);
                    if (!mergedPinGroups.TryGetValue(identity, out List<ServerPin> group))
                    {
                        group = new List<ServerPin>();
                        mergedPinGroups.Add(identity, group);
                    }
                    group.Add(pin);
                }
            }

            candidatePins.Clear();
            mergedPinGroups.Clear();

            rebuiltPins.Sort(ServerPinComparer.Instance);
            if (rebuiltPins.Count > MaximumReceivedPins)
            {
                int omitted = rebuiltPins.Count - MaximumReceivedPins;
                rebuiltPins.RemoveRange(MaximumReceivedPins, omitted);
                LogWarning($"Authoritative pin snapshot exceeded the safety limit of {MaximumReceivedPins} pins; omitted {omitted} pin(s).");
            }

            bool changed = !ListsEqual(authoritativePins, rebuiltPins);
            if (changed)
            {
                authoritativePins.Clear();
                authoritativePins.AddRange(rebuiltPins);
                ++serverRevision;
                MarkProjectionDirty();
                MaintainAuthoritativeProjection(forceAudit: true);

                LogInfo(
                    $"Authoritative sign pin list changed to revision {serverRevision}; {authoritativePins.Count} pin(s) " +
                    $"from {signZdos.Count} indexed sign ZDO(s), {mergedPins} merged duplicate(s), " +
                    $"{ignoredNonAdminPins} non-administrator sign(s) ignored.");
            }

            bool shouldBroadcast = broadcastIfChanged && IsAuthoritativeMode && (changed || serverModeBroadcastPending);
            bool broadcasted = shouldBroadcast && BroadcastSnapshot();
            if (broadcasted)
                serverModeBroadcastPending = false;

            return broadcasted;
        }

        private static bool IsMergedDuplicate(ServerPin candidate, float mergeDistance)
        {
            PinIdentity identity = new(candidate.Type, candidate.Name);
            if (!mergedPinGroups.TryGetValue(identity, out List<ServerPin> retainedPins))
                return false;

            for (int i = 0; i < retainedPins.Count; ++i)
            {
                if (Utils.DistanceXZ(candidate.Position, retainedPins[i].Position) <= mergeDistance)
                    return true;
            }

            return false;
        }

        private static bool IsAdministratorAuthor(string author)
        {
            if (string.IsNullOrEmpty(author) || !ZNet.instance)
                return false;

            if (author == "host")
                return true;

            string resolvedAuthor = author;
            RelationsManager.UpdateAuthorIfHost(author, ref resolvedAuthor);
            if (string.IsNullOrEmpty(resolvedAuthor) || ZNet.instance.m_adminList == null)
                return false;

            if (listContainsIdMethod != null)
            {
                try
                {
                    listContainsIdArguments[0] = ZNet.instance.m_adminList;
                    listContainsIdArguments[1] = resolvedAuthor;
                    return (bool)listContainsIdMethod.Invoke(ZNet.instance, listContainsIdArguments);
                }
                catch
                {
                    // Fall back to the legacy direct lookup below.
                }
            }

            return ZNet.instance.m_adminList.Contains(resolvedAuthor);
        }

        private static bool ListsEqual(List<ServerPin> left, List<ServerPin> right)
        {
            if (left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; ++i)
            {
                if (!left[i].Equals(right[i]))
                    return false;
            }

            return true;
        }

        private static void DiscoverSignPrefabs()
        {
            if (!ZNetScene.instance)
                return;

            List<GameObject> prefabs = ZNetScene.instance.m_prefabs;
            bool changed = false;
            for (int i = 0; i < prefabs.Count; ++i)
            {
                GameObject prefab = prefabs[i];
                if (prefab && prefab.GetComponent<Sign>() && signPrefabHashes.Add(prefab.name.GetStableHashCode()))
                    changed = true;
            }

            if (changed)
                RebuildSignIndex();
        }

        private static void RebuildSignIndex()
        {
            if (ZDOMan.instance == null)
                return;

            signZdos.Clear();
            foreach (KeyValuePair<ZDOID, ZDO> item in ZDOMan.instance.m_objectsByID)
            {
                ZDO zdo = item.Value;
                if (zdo != null && signPrefabHashes.Contains(zdo.GetPrefab()))
                    signZdos.Add(zdo);
            }
        }

        private static bool TryTrackSignZdo(ZDO zdo)
        {
            if (zdo == null || !signPrefabHashes.Contains(zdo.GetPrefab()))
                return false;

            signZdos.Add(zdo);
            return true;
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_DiscoverSigns
        {
            private static void Postfix()
            {
                DiscoverSignPrefabs();
                if (ZNet.instance && ZNet.instance.IsServer())
                {
                    RebuildSignIndex();
                    MarkDirty(immediate: true);
                }
            }
        }

        [HarmonyPatch]
        private static class ZDOMan_Load_IndexSigns
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.Load));
                yield return AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.LoadChunks));
            }

            private static void Postfix()
            {
                if (!ZNet.instance || !ZNet.instance.IsServer())
                    return;

                RebuildSignIndex();
                MarkDirty(immediate: true);
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(ZDOID), typeof(Vector3), typeof(int) })]
        private static class ZDOMan_CreateNewZDO_TrackSign
        {
            private static void Postfix(ZDO __result)
            {
                if (!ZNet.instance || !ZNet.instance.IsServer() || !TryTrackSignZdo(__result))
                    return;

                MarkDirty();
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        private static class ZDOMan_HandleDestroyedZDO_UntrackSign
        {
            private static void Prefix(ZDOMan __instance, ZDOID uid)
            {
                if (!ZNet.instance || !ZNet.instance.IsServer())
                    return;

                ZDO zdo = __instance.GetZDO(uid);
                if (zdo != null && signZdos.Remove(zdo))
                    MarkDirty();
            }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        private static class ZDO_Deserialize_TrackSignChange
        {
            private static void Postfix(ZDO __instance)
            {
                if (ZNet.instance && ZNet.instance.IsServer() && TryTrackSignZdo(__instance))
                    MarkDirty();
            }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.IncreaseDataRevision))]
        private static class ZDO_IncreaseDataRevision_TrackSignChange
        {
            private static void Postfix(ZDO __instance)
            {
                if (!SignPinMetadata.IsWritingCacheMetadata &&
                    ZNet.instance &&
                    ZNet.instance.IsServer() &&
                    TryTrackSignZdo(__instance))
                {
                    MarkDirty();
                }
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        private static class Minimap_AddPin_MarkProjectionDirty
        {
            private static void Postfix(PinType type, PinData __result)
            {
                if (isReconcilingProjection || !HasConfirmedAuthority || __result == null || !ControlsPinType(type))
                    return;

                MarkProjectionDirty();
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePin), new[] { typeof(PinData) })]
        private static class Minimap_RemovePin_MarkProjectionDirty
        {
            private static void Postfix(PinData pin)
            {
                if (isReconcilingProjection || !HasConfirmedAuthority || pin == null)
                    return;

                if (ControlsPinType(pin.m_type))
                    MarkProjectionDirty();
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.ShowPinNameInput))]
        private static class Minimap_ShowPinNameInput_PreventClientPinCreation
        {
            private static bool Prefix(Minimap __instance) => !ControlsPinType(__instance.m_selectedType);
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetMapData), new Type[] { })]
        private static class Minimap_GetMapData_PreserveClientPins
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Minimap __instance, out MapDataPinSwapState __state) =>
                __state = SwapAuthoritativePinsForClientPins(__instance);

            [HarmonyPriority(Priority.Last)]
            private static Exception Finalizer(Minimap __instance, MapDataPinSwapState __state, Exception __exception)
            {
                RestoreAuthoritativePinsAfterMapData(__instance, __state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdatePins))]
        private static class Minimap_UpdatePins_MaintainProjection
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => MaintainAuthoritativeProjection();
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapData), new[] { typeof(byte[]) })]
        private static class Minimap_SetMapData_ReconcileServerPins
        {
            private static Exception Finalizer(Minimap __instance, Exception __exception)
            {
                if (__exception == null && HasConfirmedAuthority)
                {
                    HideControlledClientPins(__instance, replaceSnapshot: true);
                    MarkProjectionDirty();
                    MaintainAuthoritativeProjection(forceAudit: true);
                }

                return __exception;
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
        private static class Minimap_AddSharedMapData_ReconcileServerPins
        {
            private static Exception Finalizer(Minimap __instance, Exception __exception)
            {
                if (__exception == null && HasConfirmedAuthority)
                {
                    HideControlledClientPins(__instance);
                    MarkProjectionDirty();
                    MaintainAuthoritativeProjection(forceAudit: true);
                }

                return __exception;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class ZNet_Shutdown_Reset
        {
            private static void Prefix() => ResetSession();
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
        private static class ZoneSystem_OnDestroy_Reset
        {
            private static void Postfix() => ResetSession();
        }
    }
}
