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
    [BepInPlugin("com.20fpsguy.femalevoice", "Female PMC Voice", "1.0.3")]
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
        // Debounced blacked state per id so a flickering limb-health read can't restart the loop.
        private readonly Dictionary<int, bool> _blackedState = new Dictionary<int, bool>();
        private readonly Dictionary<int, float> _blackedFlipSince = new Dictionary<int, float>();
        private readonly Dictionary<int, bool> _legState = new Dictionary<int, bool>();
        private readonly Dictionary<int, float> _legFlipSince = new Dictionary<int, float>();
        // Require seeing the entity healthy once before hurt breath is allowed, so the garbage
        // health that reads as "blacked" at spawn can't trigger it. Local player only.
        private readonly Dictionary<int, bool> _healthyConfirmed = new Dictionary<int, bool>();

        private const float BlackedRise = 1.5f;   // hurt this long before the loop starts
        private const float BlackedFall = 1.5f;   // clear this long before it stops
        private const float WindedDebounce = 2f;
        private const float WindedStaminaFrac = 0.25f; // winded below this stamina fraction
        private const float BreathSprintVolume = 0.9f;
        private const float BreathHurtVolume = 0.8f;
        private float _nextCheck;

        // Time.time when the raid actually started (player spawned in), from GameWorld.OnGameStarted.
        // Negative means not started. GameWorld and the player list also exist on the loading screen
        // where health reads as garbage, so the spawn guards anchor here instead of "first seen".
        internal static float RaidStartTime = float.NegativeInfinity;
        private const float SpawnSettle = 6f;

        // Ouch grunts (the `hit` category) played as one-shots while the local player runs with a
        // black limb, separate from the breath loop so pain stays audible under painkillers / winded.
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
            new Harmony("com.20fpsguy.femalevoicedeathfix").PatchAll();
            L.LogInfo("Female PMC voice fixes loaded.");
        }

        private void Update()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.3f;
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw == null) { CleanupAll(); RaidStartTime = float.NegativeInfinity; return; }

                // Do nothing until the raid has started and a short settle window has passed. Before
                // OnGameStarted we are on the loading screen; for the first SpawnSettle seconds the
                // HealthController is still initialising and can read "blacked". If the start signal
                // never fired, RaidStartTime stays negative and we fall through to the grace guards.
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

                    // Grace period: health/stamina uninitialised for ~3s after spawn.
                    if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = Time.time;
                    if ((Time.time - _firstSeen[id]) < 3f) { StopFor(id); continue; }

                    // The healthy-confirmed guard is local-only. Bots are routinely first seen already
                    // injured (they fight each other / spawn into firefights); requiring a prior healthy
                    // read there left their pain breath masked by winded. Bots use the grace + debounce.
                    // Coughing is the gut-wound sound and the running grunt is the leg one, so
                    // each is gated on its own part rather than on any blacked limb.
                    bool rawBlacked = HasBlackedPart(p, "Stomach");
                    bool rawLeg = HasBlackedPart(p, "LeftLeg", "RightLeg");
                    if (!rawBlacked && !rawLeg) _healthyConfirmed[id] = true;
                    bool blacked = DebounceBlacked(id, rawBlacked);
                    bool legBlacked = DebounceLeg(id, rawLeg);
                    if (local)
                    {
                        bool healthySeen = _healthyConfirmed.TryGetValue(id, out var hc) && hc;
                        blacked = blacked && healthySeen;
                        legBlacked = legBlacked && healthySeen;
                    }
                    bool windedRaw = IsOutOfStamina(p);

                    // Debounce winded so a brief stamina spike (e.g. grenade concussion) doesn't fire it.
                    if (windedRaw) { if (!_windedSince.ContainsKey(id)) _windedSince[id] = Time.time; }
                    else { _windedSince.Remove(id); }
                    bool winded = windedRaw && _windedSince.TryGetValue(id, out var ws) && (Time.time - ws) >= WindedDebounce;

                    AudioClip want;
                    if (local)
                    {
                        // The game handles winded breath for the local player; only manage hurt here.
                        // Painkillers mask the pain, so they silence the hurt loop too.
                        want = (blacked && !winded && !HasPainkiller(p)) ? hurtClip : null;
                        // Ouch grunts while running on a black leg, audible even under painkiller.
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

        // Flip the blacked state only after the raw read has disagreed for Rise/Fall seconds.
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

        // Called from the OnGameStarted patch to discard tracking populated on the loading screen,
        // so the spawn guards re-evaluate from the real spawn moment.
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

        // Involuntary reactions that are always allowed, even mid-pain. Everything else (tactical
        // callouts, mumbles) is suppressed while the local player is badly hurt.
        private static readonly HashSet<EPhraseTrigger> PainAllowed = new HashSet<EPhraseTrigger>
        {
            EPhraseTrigger.OnBeingHurt,
            EPhraseTrigger.OnAgony,
            EPhraseTrigger.OnDeath,
            EPhraseTrigger.OnBreath,
        };

        // True when the local player is too hurt for voluntary callouts: hurt-breath loop active or
        // an unmasked pain effect present.
        internal static bool LocalSuppressTacticalCallouts(EPhraseTrigger trig)
        {
            if (PainAllowed.Contains(trig)) return false;
            var p = LocalPlayer;
            if (p == null) return false;
            return HasActiveBreath(p) || IsInPain(p);
        }

        internal static object GetMember(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var name in names)
            {
                var pi = t.GetProperty(name, AllInst);
                if (pi != null) { try { return pi.GetValue(obj); } catch { } }
                var fi = t.GetField(name, AllInst);
                if (fi != null) { try { return fi.GetValue(obj); } catch { } }
            }
            return null;
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

        private static readonly string[] PainLimbs = { "LeftArm", "RightArm", "LeftLeg", "RightLeg", "Stomach" };
        private static readonly string[] VitalLimbs = { "Head", "Chest" };

        private static bool IsVitallyAlive(Player p)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return true;
                var m = hc.GetType().GetMethod("GetBodyPartHealth", AllInst);
                if (m == null) return true;
                var ps = m.GetParameters();
                var ebpType = ps[0].ParameterType;
                foreach (var name in VitalLimbs)
                {
                    object bp; try { bp = Enum.Parse(ebpType, name); } catch { continue; }
                    object hv; try { hv = ps.Length >= 2 ? m.Invoke(hc, new object[] { bp, false }) : m.Invoke(hc, new object[] { bp }); } catch { continue; }
                    var curO = GetMember(hv, "Current");
                    var maxO = GetMember(hv, "Maximum");
                    if (curO == null || maxO == null) continue;
                    if (Convert.ToSingle(maxO) > 0f && Convert.ToSingle(curO) <= 0f) return false;
                }
                return true;
            }
            catch { return true; }
        }

        internal static bool HasBlackedLimb(Player p, int minCount = 1)
            => BlackedCount(p, PainLimbs) >= minCount;

        // The game ties each sound to a specific part: coughs to a blacked stomach, running
        // grunts to a blacked leg. Counting any limb made both fire for a blacked arm too.
        internal static bool HasBlackedPart(Player p, params string[] parts)
            => BlackedCount(p, parts) >= 1;

        private static int BlackedCount(Player p, string[] parts)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return 0;
                var m = hc.GetType().GetMethod("GetBodyPartHealth", AllInst);
                if (m == null) return 0;
                var ps = m.GetParameters();
                var ebpType = ps[0].ParameterType;
                int count = 0;
                foreach (var name in parts)
                {
                    object bp; try { bp = Enum.Parse(ebpType, name); } catch { continue; }
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

        // Is the plugin currently looping a breath clip for this player?
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

        // True when the player has an active pain effect and no painkiller masking it. Returns false
        // if the effect API can't be read, so we never wrongly mute.
        internal static bool IsInPain(Player p)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return false;
                var m = hc.GetType().GetMethods(AllInst)
                    .FirstOrDefault(x => x.Name == "GetAllEffects" && x.GetParameters().Length == 0);
                if (m == null) return false;
                object res;
                try { res = m.Invoke(hc, null); } catch { res = null; }
                if (!(res is IEnumerable effects)) return false;
                bool pain = false, killer = false;
                foreach (var e in effects)
                {
                    if (e == null) continue;
                    // Effect classes are obfuscated; ToString() usually gives the readable name.
                    string id = (e.ToString() ?? "") + "|" + e.GetType().Name + "|" +
                                ((GetMember(e, "Type") as Type)?.Name ?? "");
                    if (id.IndexOf("painkiller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        id.IndexOf("analg", StringComparison.OrdinalIgnoreCase) >= 0) killer = true;
                    else if (id.IndexOf("pain", StringComparison.OrdinalIgnoreCase) >= 0) pain = true;
                }
                return pain && !killer;
            }
            catch { return false; }
        }

        // True only when a painkiller/analgesic effect is present. Returns false if the effect API
        // can't be read, so painkillers only ever silence the hurt loop when actually detected.
        internal static bool HasPainkiller(Player p)
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return false;
                var m = hc.GetType().GetMethods(AllInst)
                    .FirstOrDefault(x => x.Name == "GetAllEffects" && x.GetParameters().Length == 0);
                if (m == null) return false;
                object res;
                try { res = m.Invoke(hc, null); } catch { res = null; }
                if (!(res is IEnumerable effects)) return false;
                foreach (var e in effects)
                {
                    if (e == null) continue;
                    string id = (e.ToString() ?? "") + "|" + e.GetType().Name + "|" +
                                ((GetMember(e, "Type") as Type)?.Name ?? "");
                    if (id.IndexOf("painkiller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        id.IndexOf("analg", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return false; }
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

        // Periodic ouch grunt while the local player runs with a black limb. Plays a random `hit`
        // clip as a 2D one-shot on its own cooldown, bypassing the callout-mute hook.
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

    // Capture the real raid-start moment. GameWorld.OnGameStarted fires when the player spawns into
    // the map, which is the anchor for the spawn-settle guard.
    [HarmonyPatch]
    internal static class GameStartedPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(GameWorld), "OnGameStarted");

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

        [HarmonyPrefix]
        static bool Prefix(object __instance, EPhraseTrigger __0)
        {
            // Only gate the local player's own speaker.
            if (!ReferenceEquals(__instance, Plugin.LocalSpeaker)) return true;

            // OnBreath: winded-debounce so a brief stamina dip doesn't trigger it.
            if (__0 == EPhraseTrigger.OnBreath)
                return Plugin.Instance?.IsLocalWindedDebounced() ?? true;

            // Suppress voluntary tactical callouts while badly hurt / in pain.
            if (Plugin.LocalSuppressTacticalCallouts(__0)) return false;
            return true;
        }
    }

    [HarmonyPatch]
    internal static class DeathScreamPatch
    {
        private static readonly EPhraseTrigger[] DeathTriggers = { EPhraseTrigger.OnDeath, EPhraseTrigger.OnAgony };

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "OnDead");

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
                bool local = Plugin.GetMember(__instance, "IsYourPlayer") is bool b && b;
                Vector3 pos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                if (Plugin.GetMember(__instance, "Position") is Vector3 v) pos = v;
                PlayDeathScream(clip, pos, local);
                __state = true;
            }
            catch (Exception e) { Plugin.L.LogError("[DS] " + e); }
        }

        private static void PlayDeathScream(AudioClip clip, Vector3 pos, bool local)
        {
            var go = new GameObject("FemalePMC_DeathScream");
            go.transform.position = pos;
            // Your own death tears the raid down within a second: the scene unloads and the
            // listener gets ducked/paused, which chopped the scream off mid-vowel.
            UnityEngine.Object.DontDestroyOnLoad(go);
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.ignoreListenerPause = true;
            src.ignoreListenerVolume = true;
            src.bypassListenerEffects = true;
            src.bypassReverbZones = true;
            if (local)
            {
                // Your own death: 2D, clear but not blasting.
                src.spatialBlend = 0f;
                src.volume = 0.75f;
            }
            else
            {
                // A bot's death: 3D at its position with distance falloff.
                src.spatialBlend = 1f;
                src.volume = 0.8f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 3f;
                src.maxDistance = 60f;
            }
            src.Play();
            // Destroy(go, t) runs on scaled time and the object now survives scene loads, so the
            // teardown is driven by a real-time coroutine on the plugin instead.
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(CleanupScream(go, clip));
            else UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        // Real-time teardown: Destroy(go, t) runs on scaled time, and the object now survives
        // scene loads, so a frozen or unloading raid would leave it behind.
        private static IEnumerator CleanupScream(GameObject go, AudioClip clip)
        {
            float deadline = Time.realtimeSinceStartup + clip.length + 0.5f;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        // Only silence the speaker when we played our own scream, otherwise this mutes the
        // game's native death phrase and nothing is heard at all.
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
