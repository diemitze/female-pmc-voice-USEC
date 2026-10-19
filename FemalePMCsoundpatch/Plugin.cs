using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace FemalePMCSoundPatch
{
    [BepInPlugin("com.20fpsguy.femalepmcvoice", "Female PMC Voice", "1.0.6")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal const string VoiceClipPrefix = "FemalePMC_";
        internal static object LocalSpeaker;
        internal static Player LocalPlayer;

        private readonly Dictionary<int, AudioSource> _breath = new Dictionary<int, AudioSource>();
        private readonly Dictionary<int, Player> _breathOwner = new Dictionary<int, Player>();
        private readonly Dictionary<int, float> _firstSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _windedSince = new Dictionary<int, float>();
        // Debounced, so a flickering limb-health read can't restart the loop.
        private readonly Dictionary<int, bool> _blackedState = new Dictionary<int, bool>();
        private readonly Dictionary<int, float> _blackedFlipSince = new Dictionary<int, float>();
        private readonly Dictionary<int, bool> _legState = new Dictionary<int, bool>();
        private readonly Dictionary<int, float> _legFlipSince = new Dictionary<int, float>();
        // Health reads as garbage ("blacked") at spawn, so hurt breath waits for one healthy read.
        private readonly Dictionary<int, bool> _healthyConfirmed = new Dictionary<int, bool>();

        private const float BlackedRise = 1.5f;   // hurt this long before the loop starts
        private const float BlackedFall = 1.5f;   // clear this long before it stops
        private const float WindedDebounce = 2f;
        private const float WindedStaminaFrac = 0.25f;
        private const float BreathSprintVolume = 0.9f;
        private const float BreathHurtVolume = 0.8f;
        private float _nextCheck;

        // GameWorld already exists on the loading screen, so the spawn guards anchor to the real
        // spawn (GameWorld.OnGameStarted) instead. Negative = not started yet.
        internal static float RaidStartTime = float.NegativeInfinity;
        private const float SpawnSettle = 6f;

        // Kept apart from the breath loop so pain stays audible under painkillers or while winded.
        private float _nextLocalHit;
        private AudioSource _localHitSrc;
        private const float HitGruntMinGap = 3f;
        private const float HitGruntMaxGap = 6f;
        private const float HitGruntVolume = 0.9f;
        private const float MovingSqrSpeed = 9f; // (3 m/s)^2 horizontal: running, not walking

        private const BindingFlags AllInst =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private void Awake()
        {
            Instance = this;
            L = Logger;
            new Harmony("com.20fpsguy.femalepmcvoice.patches").PatchAll();
            L.LogInfo("Female PMC voice fixes loaded.");
        }

        // A null target makes PatchAll throw and skip every patch after it, so each patch
        // checks its own target and is skipped alone if a game update renamed it.
        internal static bool CanPatch(MethodBase target, string patch)
        {
            if (target != null) return true;
            L?.LogWarning($"{patch}: target method not found, patch skipped.");
            return false;
        }

        private void Update()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.3f;
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw == null) { CleanupAll(); RaidStartTime = float.NegativeInfinity; return; }

                // HealthController is still initialising for a few seconds after spawn. If the start
                // signal never fired we fall through to the per-player grace guards below.
                if (RaidStartTime >= 0f && (Time.time - RaidStartTime) < SpawnSettle) return;

                var players = new List<Player>();
                var list = GetMember(gw, "AllAlivePlayersList", "RegisteredPlayers", "AllPlayersEverExisted", "Players", "AllAlivePlayers") as IEnumerable;
                if (list != null)
                    foreach (var o in list)
                        if (o is Player lp) players.Add(lp);
                var mainPlayer = GetMember(gw, "MainPlayer", "mainPlayer") as Player;
                if (mainPlayer != null && !players.Contains(mainPlayer))
                    players.Add(mainPlayer);

                int mainRawId = mainPlayer != null ? PlayerId(mainPlayer) : int.MinValue;

                var seen = new HashSet<int>();
                foreach (var p in players)
                {
                    if (p == null || !IsAlive(p)) continue;
                    var hurtClip = FindBreathClip(p, "breath_hurt");
                    var sprintClip = FindBreathClip(p, "breath_sprint");
                    if (hurtClip == null && sprintClip == null) continue;
                    int rawId = PlayerId(p);
                    bool local = IsYourPlayer(p) || ReferenceEquals(p, mainPlayer) || rawId == mainRawId;
                    int id = local ? 0 : rawId;
                    if (!seen.Add(id)) continue;
                    if (local) { LocalSpeaker = GetMember(p, "Speaker"); LocalPlayer = p; }

                    if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = Time.time;
                    if ((Time.time - _firstSeen[id]) < 3f) { StopFor(id); continue; }

                    // The game ties coughing to the stomach and the running grunt to the legs.
                    bool rawBlacked = HasBlackedPart(p, "Stomach");
                    bool rawLeg = HasBlackedPart(p, "LeftLeg", "RightLeg");
                    if (!rawBlacked && !rawLeg) _healthyConfirmed[id] = true;
                    bool blacked = DebounceBlacked(id, rawBlacked);
                    bool legBlacked = DebounceLeg(id, rawLeg);
                    // Local only: bots are often first seen already hurt, and waiting for a healthy
                    // read left their pain breath masked by winded.
                    if (local)
                    {
                        bool healthySeen = _healthyConfirmed.TryGetValue(id, out var hc) && hc;
                        blacked = blacked && healthySeen;
                        legBlacked = legBlacked && healthySeen;
                    }
                    bool windedRaw = IsOutOfStamina(p);

                    // Debounced so a brief dip (e.g. grenade concussion) doesn't fire it.
                    if (windedRaw) { if (!_windedSince.ContainsKey(id)) _windedSince[id] = Time.time; }
                    else { _windedSince.Remove(id); }
                    bool winded = windedRaw && _windedSince.TryGetValue(id, out var ws) && (Time.time - ws) >= WindedDebounce;

                    AudioClip want;
                    if (local)
                    {
                        // The game already does winded breath for the local player.
                        want = (blacked && !winded && !HasPainkiller(p)) ? hurtClip : null;
                        HandleLocalHitGrunts(p, legBlacked);
                    }
                    else
                    {
                        if (!IsVitallyAlive(p)) { StopFor(id); continue; }
                        want = blacked ? hurtClip : (winded ? sprintClip : null);
                    }
                    if (want != null) EnsurePlaying(id, p, want);
                    else StopFor(id);
                }

                foreach (var id in _breath.Keys.ToList())
                {
                    if (seen.Contains(id)) continue;
                    _breathOwner.TryGetValue(id, out var owner);
                    if (owner == null || !IsAlive(owner)) RemoveFor(id);
                }
            }
            catch (Exception e) { L.LogError("[Breath] " + e); }
        }

        private bool DebounceBlacked(int id, bool raw)
            => Debounce(_blackedState, _blackedFlipSince, id, raw);

        private bool DebounceLeg(int id, bool raw)
            => Debounce(_legState, _legFlipSince, id, raw);

        private static bool Debounce(Dictionary<int, bool> state, Dictionary<int, float> flipSince, int id, bool raw)
        {
            if (!state.TryGetValue(id, out var cur)) { cur = false; state[id] = false; }
            if (raw == cur) { flipSince.Remove(id); return cur; }
            if (!flipSince.TryGetValue(id, out var since)) { since = Time.time; flipSince[id] = since; }
            float need = raw ? BlackedRise : BlackedFall;
            if (Time.time - since >= need) { state[id] = raw; flipSince.Remove(id); return raw; }
            return cur;
        }

        public void KillBreath(Player p)
        {
            try
            {
                if (p == null) return;
                RemoveFor(PlayerId(p));
                foreach (var kv in _breathOwner.ToList())
                    if (ReferenceEquals(kv.Value, p)) RemoveFor(kv.Key);
            }
            catch { }
        }

        private void EnsurePlaying(int id, Player p, AudioClip clip)
        {
            if (!_breath.TryGetValue(id, out var src) || src == null)
            {
                var go = new GameObject("FemalePMC_Breath");
                var host = p as Component;
                if (host != null) go.transform.SetParent(host.transform, false);

                src = go.AddComponent<AudioSource>();
                src.loop = true;
                src.playOnAwake = false;
                src.clip = clip;
                src.volume = clip.name.IndexOf("sprint", StringComparison.OrdinalIgnoreCase) >= 0
                    ? BreathSprintVolume : BreathHurtVolume;

                bool local = IsYourPlayer(p);
                src.spatialBlend = local ? 0f : 1f;
                if (!local)
                {
                    src.minDistance = 3f;
                    src.maxDistance = 50f;
                    src.rolloffMode = AudioRolloffMode.Linear;
                }
                _breath[id] = src;
            }
            _breathOwner[id] = p;
            if (src.clip != clip) { src.Stop(); src.clip = clip; }
            if (!src.isPlaying) src.Play();
        }

        private void StopFor(int id)
        {
            if (_breath.TryGetValue(id, out var src) && src != null && src.isPlaying) src.Stop();
        }

        private void RemoveFor(int id)
        {
            if (_breath.TryGetValue(id, out var src) && src != null)
                UnityEngine.Object.Destroy(src.gameObject);
            _breath.Remove(id);
            _breathOwner.Remove(id);
            _firstSeen.Remove(id);
            _windedSince.Remove(id);
            _blackedState.Remove(id);
            _blackedFlipSince.Remove(id);
            _legState.Remove(id);
            _legFlipSince.Remove(id);
            _healthyConfirmed.Remove(id);
        }

        // Drops tracking gathered on the loading screen.
        internal void ResetForNewRaid() => CleanupAll();

        private void CleanupAll()
        {
            foreach (var kv in _breath)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value.gameObject);
            _breath.Clear();
            _breathOwner.Clear();
            _firstSeen.Clear();
            _windedSince.Clear();
            _blackedState.Clear();
            _blackedFlipSince.Clear();
            _legState.Clear();
            _legFlipSince.Clear();
            _healthyConfirmed.Clear();
            LocalSpeaker = null;
            LocalPlayer = null;
            if (_localHitSrc != null) UnityEngine.Object.Destroy(_localHitSrc.gameObject);
            _localHitSrc = null;
            _nextLocalHit = 0f;
        }

        // Involuntary sounds; every other phrase is muted while the local player is badly hurt.
        private static readonly HashSet<EPhraseTrigger> PainAllowed = new HashSet<EPhraseTrigger>
        {
            EPhraseTrigger.OnBeingHurt,
            EPhraseTrigger.OnAgony,
            EPhraseTrigger.OnDeath,
            EPhraseTrigger.OnBreath,
        };

        internal static bool LocalSuppressTacticalCallouts(EPhraseTrigger trig)
        {
            if (PainAllowed.Contains(trig)) return false;
            var p = LocalPlayer;
            if (p == null) return false;
            return HasActiveBreath(p) || IsInPain(p);
        }

        // Lookups run every tick for every player, so resolve each (type, name) only once.
        private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> MemberCache =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();
        private static readonly Dictionary<Type, MethodInfo> BodyPartHealthCache = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> AllEffectsCache = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<(Type, string), object> EnumCache = new Dictionary<(Type, string), object>();

        private static MemberInfo ResolveMember(Type t, string name)
        {
            if (!MemberCache.TryGetValue(t, out var byName))
                MemberCache[t] = byName = new Dictionary<string, MemberInfo>();
            if (!byName.TryGetValue(name, out var mi))
            {
                mi = (MemberInfo)t.GetProperty(name, AllInst) ?? t.GetField(name, AllInst);
                byName[name] = mi;
            }
            return mi;
        }

        internal static object GetMember(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var name in names)
            {
                var mi = ResolveMember(t, name);
                if (mi is PropertyInfo pi) { try { return pi.GetValue(obj); } catch { } }
                else if (mi is FieldInfo fi) { try { return fi.GetValue(obj); } catch { } }
            }
            return null;
        }

        private static MethodInfo GetBodyPartHealthMethod(object hc)
        {
            var t = hc.GetType();
            if (!BodyPartHealthCache.TryGetValue(t, out var m))
                BodyPartHealthCache[t] = m = t.GetMethod("GetBodyPartHealth", AllInst);
            return m;
        }

        private static MethodInfo GetAllEffectsMethod(object hc)
        {
            var t = hc.GetType();
            if (!AllEffectsCache.TryGetValue(t, out var m))
                AllEffectsCache[t] = m = t.GetMethods(AllInst)
                    .FirstOrDefault(x => x.Name == "GetAllEffects" && x.GetParameters().Length == 0);
            return m;
        }

        private static object ParseEnumCached(Type enumType, string name)
        {
            var key = (enumType, name);
            if (!EnumCache.TryGetValue(key, out var v))
            {
                try { v = Enum.Parse(enumType, name); } catch { v = null; }
                EnumCache[key] = v;
            }
            return v;
        }

        private static int PlayerId(Player p)
        {
            var id = GetMember(p, "Id");
            if (id != null) { try { return Convert.ToInt32(id); } catch { } }
            return p.GetHashCode();
        }

        private static bool IsYourPlayer(Player p) => GetMember(p, "IsYourPlayer") is bool b && b;

        private static bool IsAlive(Player p)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                return hc != null && GetMember(hc, "IsAlive") is bool b && b;
            }
            catch { return false; }
        }

        private static readonly string[] VitalLimbs = { "Head", "Chest" };

        private static bool IsVitallyAlive(Player p) => ZeroedCount(p, VitalLimbs) == 0;

        internal static bool HasBlackedPart(Player p, params string[] parts) => ZeroedCount(p, parts) > 0;

        // Parts at 0 HP. Unreadable health counts as none, so nothing is ever wrongly marked hurt.
        private static int ZeroedCount(Player p, string[] parts)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return 0;
                var m = GetBodyPartHealthMethod(hc);
                if (m == null) return 0;
                var ps = m.GetParameters();
                var ebpType = ps[0].ParameterType;
                int count = 0;
                foreach (var name in parts)
                {
                    var bp = ParseEnumCached(ebpType, name); if (bp == null) continue;
                    object hv; try { hv = ps.Length >= 2 ? m.Invoke(hc, new object[] { bp, false }) : m.Invoke(hc, new object[] { bp }); } catch { continue; }
                    var curO = GetMember(hv, "Current");
                    var maxO = GetMember(hv, "Maximum");
                    if (curO == null || maxO == null) continue;
                    if (Convert.ToSingle(maxO) > 0f && Convert.ToSingle(curO) <= 0f) count++;
                }
                return count;
            }
            catch { return 0; }
        }

        internal static bool HasActiveBreath(Player p)
        {
            try
            {
                if (Instance == null || p == null) return false;
                int id = IsYourPlayer(p) ? 0 : PlayerId(p);
                return Instance._breath.TryGetValue(id, out var s) && s != null && s.isPlaying;
            }
            catch { return false; }
        }

        // Both return false when effects can't be read, so a failed read never mutes anything.
        internal static bool IsInPain(Player p)
        {
            var ids = EffectIds(p);
            return !ids.Any(IsPainkiller) && ids.Any(id => id.IndexOf("pain", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool HasPainkiller(Player p) => EffectIds(p).Any(IsPainkiller);

        private static bool IsPainkiller(string id) =>
            id.IndexOf("painkiller", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("analg", StringComparison.OrdinalIgnoreCase) >= 0;

        // Effect classes are obfuscated; ToString() usually gives the readable name.
        private static List<string> EffectIds(Player p)
        {
            var ids = new List<string>();
            try
            {
                var hc = GetMember(p, "HealthController");
                var m = hc != null ? GetAllEffectsMethod(hc) : null;
                if (m == null) return ids;
                object res;
                try { res = m.Invoke(hc, null); } catch { return ids; }
                if (!(res is IEnumerable effects)) return ids;
                foreach (var e in effects)
                    if (e != null)
                        ids.Add((e.ToString() ?? "") + "|" + e.GetType().Name + "|" + ((GetMember(e, "Type") as Type)?.Name ?? ""));
            }
            catch { }
            return ids;
        }

        private static float? UnwrapFloat(object o)
        {
            if (o == null) return null;
            if (o is float f) return f;
            if (o is double d) return (float)d;
            if (o is IConvertible ic) { try { return ic.ToSingle(null); } catch { } }
            var inner = GetMember(o, "Value", "Current", "Float", "_value", "floatValue");
            if (inner != null && !ReferenceEquals(inner, o)) return UnwrapFloat(inner);
            return null;
        }

        private static bool IsOutOfStamina(Player p)
        {
            var phys = GetMember(p, "Physical");
            if (phys == null) return false;
            var stam = GetMember(phys, "Stamina");
            if (stam == null) return false;
            float? cur = UnwrapFloat(GetMember(stam, "Current"));
            float? cap = UnwrapFloat(GetMember(stam, "TotalCapacity", "Capacity", "Maximum", "Max", "BaseValue"));
            if (cur == null || cap == null) return false;
            return cap.Value > 0f && (cur.Value / cap.Value) < WindedStaminaFrac;
        }

        // Bypasses the callout-mute hook on purpose.
        private void HandleLocalHitGrunts(Player p, bool blacked)
        {
            if (!blacked || !IsMovingLocal(p)) return;
            if (Time.time < _nextLocalHit) return;
            var clip = FindHitClip(p);
            if (clip == null) { _nextLocalHit = Time.time + HitGruntMaxGap; return; }

            if (_localHitSrc == null)
            {
                var go = new GameObject("FemalePMC_HitGrunt");
                var host = p as Component;
                if (host != null) go.transform.SetParent(host.transform, false);
                _localHitSrc = go.AddComponent<AudioSource>();
                _localHitSrc.spatialBlend = 0f;
                _localHitSrc.playOnAwake = false;
            }
            _localHitSrc.PlayOneShot(clip, HitGruntVolume);
            _nextLocalHit = Time.time + clip.length + UnityEngine.Random.Range(HitGruntMinGap, HitGruntMaxGap);
        }

        private static bool IsMovingLocal(Player p)
        {
            var vel = GetMember(p, "Velocity");
            if (vel is Vector3 v) return new Vector2(v.x, v.z).sqrMagnitude > MovingSqrSpeed;
            return false;
        }

        private static AudioClip FindHitClip(Player p)
        {
            try
            {
                var speaker = GetMember(p, "Speaker");
                if (speaker == null) return null;
                if (!(GetMember(speaker, "PhrasesBanks") is IDictionary dict)) return null;
                var pool = new List<AudioClip>();
                foreach (var key in dict.Keys)
                {
                    var clips = GetMember(dict[key], "Clips") as Array;
                    if (clips == null) continue;
                    foreach (var tagged in clips)
                    {
                        var ac = GetMember(tagged, "Clip") as AudioClip;
                        // FemalePMC_hit are the self-pain grunts; exclude FemalePMC_enemy_hit.
                        if (ac != null && ac.name.StartsWith(VoiceClipPrefix + "hit", StringComparison.OrdinalIgnoreCase))
                            pool.Add(ac);
                    }
                }
                if (pool.Count == 0) return null;
                return pool[UnityEngine.Random.Range(0, pool.Count)];
            }
            catch { return null; }
        }

        private static AudioClip FindBreathClip(Player p, string suffix)
        {
            try
            {
                var speaker = GetMember(p, "Speaker");
                if (speaker == null) return null;
                if (!(GetMember(speaker, "PhrasesBanks") is IDictionary dict) || !dict.Contains(EPhraseTrigger.OnBreath)) return null;
                var clips = GetMember(dict[EPhraseTrigger.OnBreath], "Clips") as Array;
                if (clips == null) return null;
                foreach (var tagged in clips)
                {
                    var ac = GetMember(tagged, "Clip") as AudioClip;
                    if (ac != null
                        && ac.name.StartsWith(VoiceClipPrefix, StringComparison.OrdinalIgnoreCase)
                        && ac.name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ac;
                }
                return null;
            }
            catch { return null; }
        }

        internal bool IsLocalWindedDebounced() =>
            _windedSince.TryGetValue(0, out var ws) && (Time.time - ws) >= WindedDebounce;
    }

    [HarmonyPatch]
    internal static class GameStartedPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(GameWorld), "OnGameStarted");

        static bool Prepare() => Plugin.CanPatch(TargetMethod(), nameof(GameStartedPatch));

        [HarmonyPostfix]
        static void Postfix()
        {
            Plugin.RaidStartTime = Time.time;
            Plugin.Instance?.ResetForNewRaid();
            Plugin.L?.LogDebug("[Breath] Raid started; spawn-settle window begins.");
        }
    }

    [HarmonyPatch]
    internal static class NativeBreathDebouncePatch
    {
        static MethodBase TargetMethod()
        {
            var speakerType = AccessTools.TypeByName("BaseSpeaker");
            if (speakerType == null) return null;
            return speakerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Play"
                    && m.GetParameters().Length > 0
                    && m.GetParameters()[0].ParameterType == typeof(EPhraseTrigger));
        }

        static bool Prepare() => Plugin.CanPatch(TargetMethod(), nameof(NativeBreathDebouncePatch));

        [HarmonyPrefix]
        static bool Prefix(object __instance, EPhraseTrigger __0)
        {
            if (!ReferenceEquals(__instance, Plugin.LocalSpeaker)) return true;

            if (__0 == EPhraseTrigger.OnBreath)
                return Plugin.Instance?.IsLocalWindedDebounced() ?? true;

            if (Plugin.LocalSuppressTacticalCallouts(__0)) return false;
            return true;
        }
    }

    [HarmonyPatch]
    internal static class DeathScreamPatch
    {
        private static readonly EPhraseTrigger[] DeathTriggers = { EPhraseTrigger.OnDeath, EPhraseTrigger.OnAgony };

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "OnDead");

        private static bool Prepare() => Plugin.CanPatch(TargetMethod(), nameof(DeathScreamPatch));

        [HarmonyPrefix]
        private static void Prefix(Player __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (__instance == null) return;

                Plugin.Instance?.KillBreath(__instance);

                var speaker = Plugin.GetMember(__instance, "Speaker");
                if (speaker == null) return;
                if (!(Plugin.GetMember(speaker, "PhrasesBanks") is IDictionary dict)) return;

                var pool = new List<AudioClip>();
                foreach (var trig in DeathTriggers)
                {
                    if (!dict.Contains(trig)) continue;
                    var clips = Plugin.GetMember(dict[trig], "Clips") as Array;
                    if (clips == null) continue;
                    foreach (var tagged in clips)
                    {
                        var ac = Plugin.GetMember(tagged, "Clip") as AudioClip;
                        if (ac != null) pool.Add(ac);
                    }
                }
                if (pool.Count == 0) return;
                if (!pool[0].name.StartsWith(Plugin.VoiceClipPrefix, StringComparison.OrdinalIgnoreCase)) return;

                var clip = pool[UnityEngine.Random.Range(0, pool.Count)];
                bool local = __instance.IsYourPlayer;
                // Never fall back to the camera: a failed lookup made bot screams play at the listener.
                Vector3 pos = __instance.Transform != null ? __instance.Transform.position : __instance.Position;
                PlayDeathScream(__instance, clip, pos + Vector3.up * 1.4f, local);
                __state = true;
            }
            catch (Exception e) { Plugin.L.LogError("[DS] " + e); }
        }

        // Native speech range; tune this if bot screams carry too far or not far enough.
        private const int BotScreamRolloff = 60;

        private static void PlayDeathScream(Player player, AudioClip clip, Vector3 pos, bool local)
        {
            // The game's own path gives the same distance curve and wall occlusion as normal voice lines.
            if (!local && MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                try
                {
                    var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                    var listener = audio.ListenerTransform;
                    float dist = listener != null ? Vector3.Distance(listener.position, pos) : 0f;
                    audio.PlayAtPoint(pos, clip, dist, BetterAudio.AudioSourceGroupType.Speech,
                        BotScreamRolloff, 1f, EOcclusionTest.Regular, null, false);
                    return;
                }
                catch (Exception e) { Plugin.L.LogWarning("[DS] PlayAtPoint failed, using fallback: " + e.Message); }
            }

            var go = new GameObject("FemalePMC_DeathScream");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            if (local)
            {
                // The raid tears down within a second of your death and chopped the scream off.
                UnityEngine.Object.DontDestroyOnLoad(go);
                src.ignoreListenerPause = true;
                src.ignoreListenerVolume = true;
                src.bypassListenerEffects = true;
                src.bypassReverbZones = true;
                src.spatialBlend = 0f;
                src.volume = 0.75f;
            }
            else
            {
                // Bypassing the mixer ignored the volume slider and muffling, so under Fika's death
                // fade, bots dying after you screamed at full volume over a muted world.
                src.outputAudioMixerGroup = SpeechMixerFor(player);
                src.spatialBlend = 1f;
                src.spread = 0f;
                src.dopplerLevel = 0f;
                src.volume = 0.8f;
                // Linear 3-60m kept a 40m kill at a third of full volume, so it read as right next to you.
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 1.5f;
                src.maxDistance = 70f;
            }
            src.Play();
            if (!local)
            {
                UnityEngine.Object.Destroy(go, clip.length + 0.5f);
                return;
            }
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(CleanupScream(go, clip));
            else UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        // Same choice Player.UpdateMuffledState makes for an observed player's speech.
        private static UnityEngine.Audio.AudioMixerGroup SpeechMixerFor(Player player)
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated) return null;
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            bool muffled = Plugin.GetMember(player, "Muffled") is bool m && m;
            return muffled ? audio.SimpleOccludedMixerGroup : audio.ObservedPlayerSpeechMixer;
        }

        // Destroy(go, t) runs on scaled time, which a frozen or unloading raid never finishes.
        private static IEnumerator CleanupScream(GameObject go, AudioClip clip)
        {
            float deadline = Time.realtimeSinceStartup + clip.length + 0.5f;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        // Only when we played our own scream, or the native one is muted and nothing plays.
        [HarmonyPostfix]
        private static void Postfix(Player __instance, bool __state)
        {
            try
            {
                if (__instance == null || !__state) return;
                var speaker = Plugin.GetMember(__instance, "Speaker");
                if (speaker == null) return;
                var t = speaker.GetType();
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.GetParameters().Length != 0) continue;
                    var n = m.Name;
                    if (n == "Shut" || n == "Shutup" || n == "Stop" || n == "StopImmediately" || n == "ForceStop" || n == "Reset")
                    { m.Invoke(speaker, null); return; }
                }
            }
            catch (Exception e) { Plugin.L.LogError("[DS] Postfix: " + e); }
        }
    }
}
