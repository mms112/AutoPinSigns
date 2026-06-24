using HarmonyLib;
using Splatform;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using static AutoPinSigns.AutoPinSigns;
using static Minimap;

namespace AutoPinSigns
{
    internal static class ServerPinSync
    {
        private const int ProtocolVersion = 1;
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
            internal readonly long Creator;
            internal readonly string Author;

            internal ServerPin(Vector3 position, PinType type, string name, long creator, string author)
            {
                Position = position;
                Type = type;
                Name = name ?? string.Empty;
                Creator = creator;
                Author = author ?? string.Empty;
            }

            public bool Equals(ServerPin other) =>
                Position == other.Position &&
                Type == other.Type &&
                Name == other.Name &&
                Creator == other.Creator &&
                Author == other.Author;
        }

        private sealed class PinDataReferenceComparer : IEqualityComparer<PinData>
        {
            internal static readonly PinDataReferenceComparer Instance = new();

            public bool Equals(PinData left, PinData right) => ReferenceEquals(left, right);

            public int GetHashCode(PinData value) => RuntimeHelpers.GetHashCode(value);
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
                result = left.Creator.CompareTo(right.Creator);
                if (result != 0) return result;
                return string.CompareOrdinal(left.Author, right.Author);
            }
        }

        private static readonly HashSet<int> signPrefabHashes = new() { "sign".GetStableHashCode() };
        private static readonly HashSet<ZDO> signZdos = new();
        private static readonly List<ZDO> staleZdos = new();

        private static readonly List<ServerPin> authoritativePins = new();
        private static readonly List<ServerPin> rebuiltPins = new();
        private static readonly List<ServerPin> receivedPins = new();
        private static readonly List<PinData> appliedPins = new();
        private static readonly HashSet<PinData> appliedPinSet = new(PinDataReferenceComparer.Instance);
        private static readonly Dictionary<long, float> nextAllowedRequestByPeer = new();

        private static ZRoutedRpc registeredRpcInstance;
        private static bool serverListDirty;
        private static bool serverModeBroadcastPending;
        private static float rebuildAt;
        private static long serverRevision;

        private static bool clientHasSnapshot;
        private static bool clientAuthorityConfirmed;
        private static long clientRevision = -1;
        private static float nextRequestAt;
        private static int clientRequestAttempts;
        private static bool clientRequestLimitLogged;
        private static bool modeStateInitialized;
        private static bool lastAuthoritativeMode;

        private static Minimap projectionMinimap;
        private static bool projectionDirty;
        private static bool isReconcilingProjection;
        private static int lastObservedPinCount = -1;
        private static float nextProjectionAuditAt;
        private static float nextProjectionLogAt;
        private static bool initialProjectionCompleted;

        private static bool HasConfirmedAuthority =>
            ZNet.instance && (ZNet.instance.IsServer() ? IsAuthoritativeMode : IsEnabled && clientAuthorityConfirmed);

        internal static bool HasActiveAuthority => HasConfirmedAuthority;

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

        private static void ApplyModeState()
        {
            LocalSignPins.RefreshAll();

            if (ZNet.instance && ZNet.instance.IsServer())
            {
                if (IsAuthoritativeMode)
                {
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
                }
                return;
            }

            if (!IsEnabled)
            {
                RemoveAppliedPins();
                receivedPins.Clear();
                clientHasSnapshot = false;
                clientAuthorityConfirmed = false;
                clientRevision = -1;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                initialProjectionCompleted = false;
                ResetProjectionTracking();
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

                return $"Auto Pin Signs: server authoritative mode, revision {serverRevision}, {authoritativePins.Count} pin(s), {appliedPins.Count} projected pin(s), {signZdos.Count} indexed sign ZDO(s).";
            }

            string confirmation = clientAuthorityConfirmed ? "ready" : clientHasSnapshot ? "server mode inactive" : "pending";
            return $"Auto Pin Signs: server authoritative probe {confirmation}, revision {clientRevision}, {receivedPins.Count} received pin(s), {appliedPins.Count} projected pin(s), request attempts {clientRequestAttempts}/{MaximumAutomaticRequestAttempts}.";
        }

        internal static void ResetSession()
        {
            RemoveAppliedPins();
            signZdos.Clear();
            staleZdos.Clear();
            authoritativePins.Clear();
            rebuiltPins.Clear();
            receivedPins.Clear();
            nextAllowedRequestByPeer.Clear();
            serverListDirty = false;
            serverModeBroadcastPending = false;
            rebuildAt = 0f;
            serverRevision = 0;
            clientHasSnapshot = false;
            clientAuthorityConfirmed = false;
            clientRevision = -1;
            nextRequestAt = 0f;
            clientRequestAttempts = 0;
            clientRequestLimitLogged = false;
            modeStateInitialized = false;
            lastAuthoritativeMode = false;
            initialProjectionCompleted = false;
            ResetProjectionTracking();
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
                if (protocol != ProtocolVersion)
                {
                    LogWarning($"Ignored Auto Pin Signs request using unsupported protocol {protocol} from peer {sender}.");
                    return;
                }

                bool changedAndBroadcast = IsAuthoritativeMode && serverListDirty && RebuildServerList(broadcastIfChanged: true);
                if (!changedAndBroadcast)
                    SendSnapshot(sender, requestedRevision);
            }
            catch (Exception exception)
            {
                LogWarning($"Failed to read Auto Pin Signs request from peer {sender}: {exception.Message}");
            }
        }

        private static void SendSnapshot(long target, long requestedRevision)
        {
            ZPackage package = CreateSnapshotPackage(requestedRevision);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, ResponseRpc, package);
        }

        private static bool BroadcastSnapshot()
        {
            if (ZRoutedRpc.instance == null || !ZNet.instance || !ZNet.instance.IsServer())
                return false;

            ZPackage package = CreateSnapshotPackage(requestedRevision: -1);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ResponseRpc, package);
            return true;
        }

        private static ZPackage CreateSnapshotPackage(long requestedRevision)
        {
            ZPackage package = new();
            package.Write(ProtocolVersion);
            package.Write(IsAuthoritativeMode);
            package.Write(serverRevision);

            bool includePins = IsAuthoritativeMode && requestedRevision != serverRevision;
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
                long revision = package.ReadLong();
                bool includesPins = package.ReadBool();

                if (!enabled)
                {
                    RemoveAppliedPins();
                    receivedPins.Clear();
                    clientHasSnapshot = true;
                    clientAuthorityConfirmed = false;
                    clientRevision = revision;
                    initialProjectionCompleted = false;
                    ResetProjectionTracking();
                    clientRequestAttempts = 0;
                    clientRequestLimitLogged = false;
                    LocalSignPins.RefreshAll();
                    return;
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
                        long creator = package.ReadLong();
                        string author = package.ReadString();

                        if (IsUserPinType(type))
                            receivedPins.Add(new ServerPin(position, type, name, creator, author));
                    }
                }

                bool newlyConfirmed = !clientAuthorityConfirmed;
                clientRevision = revision;
                clientHasSnapshot = true;
                clientAuthorityConfirmed = true;
                if (newlyConfirmed)
                    initialProjectionCompleted = false;
                clientRequestAttempts = 0;
                clientRequestLimitLogged = false;
                MarkProjectionDirty();
                MaintainAuthoritativeProjection(forceAudit: true);

                LogInfo($"Received authoritative server pins revision {clientRevision}; {receivedPins.Count} pin(s).");
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
            if (!HasConfirmedAuthority || !Minimap.instance || isReconcilingProjection)
                return;

            Minimap minimap = Minimap.instance;
            if (!ReferenceEquals(projectionMinimap, minimap))
            {
                projectionMinimap = minimap;
                appliedPins.Clear();
                appliedPinSet.Clear();
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

                if (!IsUserPinType(pin.m_type))
                    continue;

                // In authoritative mode the five standard user icon types are a projection
                // of the server snapshot. Any foreign pin using one of those types is stale.
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
                    applied.m_pos != expected.Position)
                {
                    return false;
                }
            }

            return true;
        }

        private static void RebuildProjection(Minimap minimap, List<ServerPin> source)
        {
            isReconcilingProjection = true;
            bool firstProjection = !initialProjectionCompleted;
            bool completed = false;
            int removedSavedPins = 0;

            try
            {
                List<PinData> mapPins = minimap.m_pins;
                for (int i = mapPins.Count - 1; i >= 0; --i)
                {
                    PinData pin = mapPins[i];
                    if (pin == null || !IsUserPinType(pin.m_type))
                        continue;

                    if (pin.m_save && !appliedPinSet.Contains(pin))
                        ++removedSavedPins;

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
                        isChecked: false,
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
                ReportRemovedSavedPins(removedSavedPins, firstProjection);
        }

        private static void ReportRemovedSavedPins(int removedCount, bool firstProjection)
        {
            if (removedCount <= 0)
                return;

            string message = $"Server authoritative pins removed {removedCount} saved client user pin(s).";
            if (firstProjection)
            {
                string warning = message + " Existing client pins are not restored by the mod.";
                LogWarning(warning);
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, warning, 0, null);
                nextProjectionLogAt = Time.realtimeSinceStartup + ProjectionLogIntervalSeconds;
                return;
            }

            if (Time.realtimeSinceStartup >= nextProjectionLogAt)
            {
                nextProjectionLogAt = Time.realtimeSinceStartup + ProjectionLogIntervalSeconds;
                LogInfo(message + " The authoritative projection was restored.");
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

        private static void RemoveAppliedPins()
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
            lastObservedPinCount = minimap ? minimap.m_pins.Count : -1;
        }

        private static bool RebuildServerList(bool broadcastIfChanged)
        {
            serverListDirty = false;
            rebuiltPins.Clear();
            staleZdos.Clear();

            foreach (ZDO zdo in signZdos)
            {
                if (zdo == null || !signPrefabHashes.Contains(zdo.GetPrefab()))
                {
                    staleZdos.Add(zdo);
                    continue;
                }

                if (!SignPinParser.TryParse(zdo.GetString(ZDOVars.s_text), out SignPinMatch match))
                    continue;

                rebuiltPins.Add(new ServerPin(
                    zdo.GetPosition(),
                    match.Type,
                    match.Name,
                    zdo.GetLong(ZDOVars.s_creator, 0L),
                    zdo.GetString(ZDOVars.s_author)));
            }

            for (int i = 0; i < staleZdos.Count; ++i)
                signZdos.Remove(staleZdos[i]);
            staleZdos.Clear();

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

                LogInfo($"Authoritative sign pin list changed to revision {serverRevision}; {authoritativePins.Count} pin(s) from {signZdos.Count} indexed sign ZDO(s).");
            }

            bool shouldBroadcast = broadcastIfChanged && IsAuthoritativeMode && (changed || serverModeBroadcastPending);
            bool broadcasted = shouldBroadcast && BroadcastSnapshot();
            if (broadcasted)
                serverModeBroadcastPending = false;

            return broadcasted;
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

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        private static class ZDOMan_Load_IndexSigns
        {
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
                if (ZNet.instance && ZNet.instance.IsServer() && TryTrackSignZdo(__instance))
                    MarkDirty();
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        private static class Minimap_AddPin_MarkProjectionDirty
        {
            private static void Postfix(PinType type, PinData __result)
            {
                if (isReconcilingProjection || !HasConfirmedAuthority || __result == null || !IsUserPinType(type))
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

                if (IsUserPinType(pin.m_type))
                    MarkProjectionDirty();
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.ShowPinNameInput))]
        private static class Minimap_ShowPinNameInput_PreventClientPinCreation
        {
            private static bool Prefix() => !HasConfirmedAuthority;
        }

        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static class Minimap_UpdatePins_MaintainProjection
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => MaintainAuthoritativeProjection();
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapData), new[] { typeof(byte[]) })]
        private static class Minimap_SetMapData_ReconcileServerPins
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (HasConfirmedAuthority)
                {
                    MarkProjectionDirty();
                    MaintainAuthoritativeProjection(forceAudit: true);
                }

                return __exception;
            }
        }

        [HarmonyPatch(typeof(Minimap), "AddSharedMapData")]
        private static class Minimap_AddSharedMapData_ReconcileServerPins
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (HasConfirmedAuthority)
                {
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
