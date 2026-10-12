using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace FemalePMCVoice;

/// Completes a partially-female USEC PMC's appearance into a fully-female one, so bots
/// never show up with mixed looks (e.g. a female torso and a male head).
///
/// Availability is checked at load time:
///   - If neither W.A.A.C. nor DLC.FemaleClothes is loaded, this service disables itself.
///   - W.A.A.C. provides: female heads, female voices (Nadya, Penny, Lara, Zoey) and
///     predefined full outfits.
///   - DLC.FemaleClothes provides: the USEC-only randomized clothing pool.
///
/// When both mods are loaded, clothing is picked 50/50 between a predefined set and a
/// random USEC-only mix. If only one is loaded, that source is used exclusively.

[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 3)]
public class FemalePmcAppearanceService(
    BotTable botTable,
    ISptLogger<FemalePmcAppearanceService> logger,
    RandomUtil randomUtil
) : IOnLoad
{
    // 50% predefined set, 50% random USEC mix when both mods are loaded.
    private const int PredefinedSetChancePercent = 50;

    // This mod's own voice. Always available.
    private const string FemaleUsecVoiceId = "67a1c4f2e8b3d5a09f12c7b4";

    // -------------------------------------------------------------------------
    // Voices provided by W.A.A.C.
    // -------------------------------------------------------------------------
    private static readonly List<string> WacVoiceIds =
    [
        "6749e134736238b84464bb7f", // Nadya
        "6749e135883e9b409e5504ad", // Penny
        "6749e1375921eb569cc77a28", // Lara
        "6749e1385477cbdf06903338", // Zoey
    ];

    // -------------------------------------------------------------------------
    // Female heads (provided by W.A.A.C.)
    // -------------------------------------------------------------------------
    private static readonly List<string> FemaleHeadIds =
    [
        "6749e240101f7a863857863e",
        "6749e2421d2b9dac29c8df16",
        "6749e243618fa7b43976dbbd",
        "6749e244270c65fe94b07956",
        "6749e247662574ff0fa3bf4b",
        "6749e24911cd1bb602995a75",
        "6749e24bf0c915b2c3875d0b",
        "6749e24eea7b9c198a4f781f",
        "6749e24ffb3128d15415fc64",
        "6749e251f1dfaf8b8980da5e",
        "6749e255a905c48df0c5011c",
    ];

    // -------------------------------------------------------------------------
    // Predefined outfits (provided by W.A.A.C.)
    // -------------------------------------------------------------------------
    private static readonly List<ClothingSet> PredefinedSets =
    [
        new("67480383b253d50226f3becd", "674803a3e68064334083a248", "67480396eda19f232a648533"), // Lara
        new("674803aaa746bc558c564a95", "674803b9267a8f14fd94aa45", "674803ac54617384f15d0fb5"), // Elena
        new("674803c084fc853c33cf7d6b", "674803ce19f3cb8d8a61b744", "674803c288da91d7a1ead784"), // Arabic Soldier
        new("674803dbfa8cadd56c5dea49", "674803e6e6a2a8cceff3cd11", "674803dcd8f6a3f4a153997e"), // Roze Virago
        new("674803ef6fb7d2f0b38fcc2d", "674803f59ab5902a9719a91e", "674803f0fa53fe247667efc7"), // Kleo Blackcell
        new("674803fc14cfddda2a2d86ab", "6748040491be032fda2b9c80", "674803fe3142a2853804a0f5"), // Roze Standard
        new("67480408f6e6654a7865fee7", "6748040f79b424b50780beb7", "67480409eb5288be39433343"), // Kleo Fission
        new("67480413496c17c5f5f3c7ab", "6748041a73f4d46a803ba8ee", "67480414aab2c73fc524b6a5"), // Valeria Standard
        new("6748041f4c9e10f6c3df65d5", "6748042731d67705b50e06a9", "6748042241b1c5d48e155832"), // Valeria Blackcell
        new("6748042d8cca47dd189736c2", "67480433b9078bef9c64344f", "6748042eadfa4ceed5e748f9"), // Zoey Zombie
    ];

    // -------------------------------------------------------------------------
    // USEC-only randomized pools (provided by DLC.FemaleClothes)
    // -------------------------------------------------------------------------
    private static readonly List<string> UsecTopIds =
    [
        "679ebe6eee23f42164000049", // USEC Adaptive Combat
        "679ebe6eee23f4216400004d", // USEC Mission
        "679ebe6eee23f42164000051", // USEC Urban Responder
        "679ebe6eee23f42164000055", // USEC Softshell Flexion
        "679ebe6eee23f42164000079", // USEC Woodland Infiltrator
        "679ebe6eee23f4216400007d", // USEC Boss Delta
        "679ebe6eee23f421640000ce", // USEC Agressor TAC
        "679ebe6eee23f421640000d2", // USEC Cereum
        "679ebe6eee23f421640000d6", // USEC Night Patrol
        "679ebe6eee23f421640000da", // USEC TIER2
        "679ebe6eee23f421640000fa", // USEC AC Ranger Green
        "679ebe6eee23f421640000fe", // USEC Commando
        "679ebe6eee23f42164000102", // USEC BOSS Delta MC
        "679ebe6eee23f42164000154", // USEC Sandstone
        "679ebe6eee23f42164000158", // USEC Chameleon
        "679ebe6eee23f4216400015c", // USEC PCU Ironsight
        "679ebe6eee23f42164000160", // USEC VEKTOR
        "679ebe6eee23f42164000174", // USEC Standard
    ];

    private static readonly List<string> UsecBottomIds =
    [
        "679ebe6eee23f42164000018", // USEC AC Ranger Green
        "679ebe6eee23f4216400001b", // USEC Taclife Terrain
        "679ebe6eee23f4216400001e", // USEC DesOps
        "679ebe6eee23f42164000021", // USEC Woodland Infiltrator
        "679ebe6eee23f42164000024", // USEC TIER2
        "679ebe6eee23f42164000027", // USEC Urban Responder
        "679ebe6eee23f4216400009c", // USEC Standard
        "679ebe6eee23f4216400009f", // USEC Rangemaster
        "679ebe6eee23f421640000a2", // USEC Commando
        "679ebe6eee23f421640000a5", // USEC Gen.2 Khyber
        "679ebe6eee23f421640000a8", // USEC TIER3
        "679ebe6eee23f421640000ab", // USEC Defender
        "679ebe6eee23f4216400011f", // USEC Deep Recon
        "679ebe6eee23f42164000122", // USEC Sage Warrior
        "679ebe6eee23f42164000125", // USEC K4
        "679ebe6eee23f4216400012b", // USEC Ranger Jeans
        "679ebe6eee23f4216400012e", // USEC TACRES
        "679ebe6eee23f42164000131", // USEC Defender WG
    ];

    private static readonly List<string> UsecHandsIds =
    [
        "679ebe6eee23f4216400004a",
        "679ebe6eee23f4216400004e",
        "679ebe6eee23f42164000052",
        "679ebe6eee23f42164000056",
        "679ebe6eee23f4216400007a",
        "679ebe6eee23f4216400007e",
        "679ebe6eee23f421640000cf",
        "679ebe6eee23f421640000d3",
        "679ebe6eee23f421640000d7",
        "679ebe6eee23f421640000db",
        "679ebe6eee23f421640000fb",
        "679ebe6eee23f421640000ff",
        "679ebe6eee23f42164000103",
        "679ebe6eee23f42164000155",
        "679ebe6eee23f42164000159",
        "679ebe6eee23f4216400015d",
        "679ebe6eee23f42164000161",
        "679ebe6eee23f42164000175",
    ];

    // -------------------------------------------------------------------------
    // Combined lookup sets for "is this already female?" checks.
    // -------------------------------------------------------------------------
    private static readonly HashSet<string> FemaleVoiceSet  = new(WacVoiceIds.Append(FemaleUsecVoiceId));
    private static readonly HashSet<string> FemaleHeadSet   = new(FemaleHeadIds);
    private static readonly HashSet<string> FemaleTopSet    = new(PredefinedSets.Select(s => s.TopId).Concat(UsecTopIds));
    private static readonly HashSet<string> FemaleBottomSet = new(PredefinedSets.Select(s => s.BottomId).Concat(UsecBottomIds));
    private static readonly HashSet<string> FemaleHandsSet  = new(PredefinedSets.Select(s => s.HandsId).Concat(UsecHandsIds));

    // -------------------------------------------------------------------------
    // Runtime state set in OnLoad.
    // -------------------------------------------------------------------------
    private static ISptLogger<FemalePmcAppearanceService> _logger = null!;
    private static RandomUtil _rng = null!;
    private static bool _wacAvailable;
    private static bool _dlcAvailable;
    private static List<string> _availableVoicePool = null!;

    public Task OnLoad()
    {
        _logger = logger;
        _rng = randomUtil;

        _wacAvailable = IsWacAvailable();
        _dlcAvailable = IsDlcAvailable();

        if (!_wacAvailable && !_dlcAvailable)
        {
            logger.Warning(
                "[FemalePmcAppearance] Neither W.A.A.C. nor DLC.FemaleClothes found in pmcusec pools. " +
                "Appearance completion disabled.");
            return Task.CompletedTask;
        }

        // Voice pool depends on which mods are present.
        _availableVoicePool = new List<string> { FemaleUsecVoiceId };
        if (_wacAvailable) _availableVoicePool.AddRange(WacVoiceIds);

        logger.Info(
            $"[FemalePmcAppearance] W.A.A.C.: {(_wacAvailable ? "found" : "not found")}, " +
            $"DLC.FemaleClothes: {(_dlcAvailable ? "found" : "not found")}.");

        var target = AccessTools.Method(typeof(BotGenerator), "PrepareAndGenerateBot");
        if (target is null)
        {
            logger.Warning(
                "[FemalePmcAppearance] BotGenerator.PrepareAndGenerateBot not found - " +
                "appearance completion disabled.");
            return Task.CompletedTask;
        }

        var postfix = new HarmonyMethod(
            AccessTools.Method(typeof(FemalePmcAppearanceService), nameof(PrepareAndGenerateBotPostfix)));
        new Harmony("com.20fpsguy.femalevoice.appearance").Patch(target, postfix: postfix);

        logger.Success("[FemalePmcAppearance] USEC female appearance completion patch applied.");
        return Task.CompletedTask;
    }

    // W.A.A.C. presence: at least one of its heads in the pmcusec head pool.
    // Heads come from W.A.A.C., so this is the authoritative check for that mod.
    private bool IsWacAvailable()
    {
        if (!botTable.Types.TryGetValue("pmcusec", out var pmc)) return false;
        var head = pmc?.BotAppearance?.Head;
        if (head is null) return false;
        return head.Keys.Any(k => FemaleHeadSet.Contains(k.ToString()));
    }

    // DLC.FemaleClothes presence: at least one of its USEC tops in the pmcusec body pool.
    // Clothing comes from DLC.FemaleClothes, so this is the authoritative check for that mod.
    private bool IsDlcAvailable()
    {
        if (!botTable.Types.TryGetValue("pmcusec", out var pmc)) return false;
        var body = pmc?.BotAppearance?.Body;
        if (body is null) return false;
        return body.Keys.Any(k => UsecTopIds.Contains(k.ToString()));
    }

    // If a generated USEC PMC has any female part, force the remaining parts to also be female.
    // Fully male bots are left alone; fully female bots need no changes.
    private static void PrepareAndGenerateBotPostfix(ref BotBase __result, BotGenerationDetails details)
    {
        try
        {
            if (__result is null) return;
            if (!details.IsPmc || details.Side != "Usec") return;

            var cust = __result.Customization;
            if (cust is null) return;

            // Head can only be considered female if W.A.A.C. is loaded.
            var headFemale  = _wacAvailable && FemaleHeadSet.Contains(cust.Head);
            var voiceFemale = FemaleVoiceSet.Contains(cust.Voice);
            var bodyFemale  = FemaleTopSet.Contains(cust.Body);
            var feetFemale  = FemaleBottomSet.Contains(cust.Feet);
            var handsFemale = FemaleHandsSet.Contains(cust.Hands);

            var anyFemale = headFemale || voiceFemale || bodyFemale || feetFemale || handsFemale;
            var allFemale = headFemale && voiceFemale && bodyFemale && feetFemale && handsFemale;

            // Fully male - not our case. Fully female - already done.
            if (!anyFemale || allFemale) return;

            // Head: only touch if W.A.A.C. is loaded.
            if (!headFemale && _wacAvailable)
                cust.Head = _rng.GetArrayValue(FemaleHeadIds);

            // Voice: pick from whatever is available (F.P.V. voice + W.A.A.C. if present).
            if (!voiceFemale)
                cust.Voice = _rng.GetArrayValue(_availableVoicePool);

            // Clothing: choose the source based on what is available.
            if (_wacAvailable && _dlcAvailable)
            {
                if (_rng.GetInt(0, 100) < PredefinedSetChancePercent)
                    ApplyPredefinedSet(cust);
                else
                    ApplyRandomUsecMix(cust);
            }
            else if (_wacAvailable)
            {
                ApplyPredefinedSet(cust);
            }
            else // DLC only
            {
                ApplyRandomUsecMix(cust);
            }

            _logger.Info("[FemalePmcAppearance] Completed partial female appearance on a USEC PMC.");
        }
        catch (Exception e)
        {
            _logger.Error($"[FemalePmcAppearance] Postfix error: {e.Message}");
        }
    }

    private static void ApplyPredefinedSet(Customization cust)
    {
        var set = _rng.GetArrayValue(PredefinedSets);
        cust.Body  = set.TopId;
        cust.Feet  = set.BottomId;
        cust.Hands = set.HandsId;
    }

    private static void ApplyRandomUsecMix(Customization cust)
    {
        cust.Body  = _rng.GetArrayValue(UsecTopIds);
        cust.Feet  = _rng.GetArrayValue(UsecBottomIds);
        cust.Hands = _rng.GetArrayValue(UsecHandsIds);
    }

    public record ClothingSet(string TopId, string BottomId, string HandsId);
}
