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
    [BepInPlugin("com.20fpsguy.femalevoice", "Female PMC Voice", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal const string VoiceClipPrefix = "FemalePMC_";
        internal static object LocalSpeaker;

        private readonly Dictionary<int, AudioSource> _breath = new Dictionary<int, AudioSource>();
        private readonly Dictionary<int, Player> _breathOwner = new Dictionary<int, Player>();
        private readonly Dictionary<int, float> _firstSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _windedSince = new Dictionary<int, float>();
        private const float WindedDebounce = 2f;
        private const float BreathSprintVolume = 0.5f;  // out-of-stamina clip
        private const float BreathHurtVolume   = 0.45f; // hurt-badly clip
        private float _nextCheck;

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
                if (gw == null) { CleanupAll(); return; }

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
                    if (local) LocalSpeaker = GetMember(p, "Speaker");

                    // Grace period: health/stamina uninitialised for ~3s after spawn.
                    if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = Time.time;
                    if ((Time.time - _firstSeen[id]) < 3f) { StopFor(id); continue; }

                    bool blacked = HasBlackedLimb(p);
                    bool windedRaw = IsOutOfStamina(p);

                    // Debounce winded so grenade concussion (brief spike) doesn't trigger the clip.
                    if (windedRaw) { if (!_windedSince.ContainsKey(id)) _windedSince[id] = Time.time; }
                    else { _windedSince.Remove(id); }
                    bool winded = windedRaw && _windedSince.TryGetValue(id, out var ws) && (Time.time - ws) >= WindedDebounce;

                    AudioClip want;
                    if (local)
                    {
                        // Native OnBreath handles winded for local player; only manage hurt here.
                        want = (blacked && !winded) ? hurtClip : null;
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
        }

        private void CleanupAll()
        {
            foreach (var kv in _breath)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value.gameObject);
            _breath.Clear();
            _breathOwner.Clear();
            _firstSeen.Clear();
            _windedSince.Clear();
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
        {
            try
            {
                var hc = GetMember(p, "HealthController");
                if (hc == null) return false;
                var m = hc.GetType().GetMethod("GetBodyPartHealth", AllInst);
                if (m == null) return false;
                var ps = m.GetParameters();
                var ebpType = ps[0].ParameterType;
                int count = 0;
                foreach (var name in PainLimbs)
                {
                    object bp; try { bp = Enum.Parse(ebpType, name); } catch { continue; }
                    object hv; try { hv = ps.Length >= 2 ? m.Invoke(hc, new object[] { bp, false }) : m.Invoke(hc, new object[] { bp }); } catch { continue; }
                    var curO = GetMember(hv, "Current");
                    var maxO = GetMember(hv, "Maximum");
                    if (curO == null || maxO == null) continue;
                    if (Convert.ToSingle(maxO) > 0f && Convert.ToSingle(curO) <= 0f)
                    {
                        count++;
                        if (count >= minCount) return true;
                    }
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
            return cap.Value > 0f && (cur.Value / cap.Value) < 0.35f;
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
    internal static class NativeBreathDebouncePatch
    {
        static MethodBase TargetMethod()
        {
            var speakerType = AccessTools.TypeByName("PhraseSpeakerClass");
            if (speakerType == null) return null;
            return speakerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Play"
                    && m.GetParameters().Length > 0
                    && m.GetParameters()[0].ParameterType == typeof(EPhraseTrigger));
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance, EPhraseTrigger __0)
        {
            if (__0 != EPhraseTrigger.OnBreath) return true;
            if (!ReferenceEquals(__instance, Plugin.LocalSpeaker)) return true;
            return Plugin.Instance?.IsLocalWindedDebounced() ?? true;
        }
    }

    [HarmonyPatch]
    internal static class DeathScreamPatch
    {
        private static readonly EPhraseTrigger[] DeathTriggers = { EPhraseTrigger.OnDeath, EPhraseTrigger.OnAgony };

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "OnDead");

        [HarmonyPrefix]
        private static void Prefix(Player __instance)
        {
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
                if (!Plugin.HasBlackedLimb(__instance, minCount: 2)) return;

                var clip = pool[UnityEngine.Random.Range(0, pool.Count)];
                bool local = Plugin.GetMember(__instance, "IsYourPlayer") is bool b && b;
                Vector3 pos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                if (Plugin.GetMember(__instance, "Position") is Vector3 v) pos = v;
                PlayDeathScream(clip, pos, local);
            }
            catch (Exception e) { Plugin.L.LogError("[DS] " + e); }
        }

        private static void PlayDeathScream(AudioClip clip, Vector3 pos, bool local)
        {
            var go = new GameObject("FemalePMC_DeathScream");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            if (local)
            {
                // Your own death: 2D, clear but not blasting.
                src.spatialBlend = 0f;
                src.volume = 0.75f;
            }
            else
            {
                // A bot's death: 3D at the bot's position with natural distance falloff,
                // so a nearby death isn't full volume and far ones fade out.
                src.spatialBlend = 1f;
                src.volume = 0.8f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 3f;
                src.maxDistance = 60f;
            }
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            try
            {
                if (__instance == null) return;
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
