using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using BotCustomization = SPTarkov.Server.Core.Models.Eft.Common.Tables.Customization;

namespace FemalePMCVoice;

// Turns a USEC PMC with any female part (usually the voice) fully female, so bots never get
// a female voice on a male head. Needs W.A.A.C. for the heads; DLC.FemaleClothes is optional.
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 3)]
public class FemalePmcAppearanceService(
    TemplateTable templateTable,
    ISptLogger<FemalePmcAppearanceService> logger,
    RandomUtil randomUtil
) : IOnLoad
{
    // Only used when DLC.FemaleClothes is loaded; W.A.A.C. alone always uses a predefined set.
    private const int PredefinedSetChancePercent = 50;

    private const string FemaleUsecVoiceId = "67a1c4f2e8b3d5a09f12c7b4";

    private static readonly List<string> WacVoiceIds =
    [
        "6749e134736238b84464bb7f", // Nadya
        "6749e135883e9b409e5504ad", // Penny
        "6749e1375921eb569cc77a28", // Lara
        "6749e1385477cbdf06903338", // Zoey
    ];

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

    private static readonly List<string> AllFemaleVoiceIds = [FemaleUsecVoiceId, .. WacVoiceIds];
    private static readonly HashSet<string> FemaleVoiceSet  = new(AllFemaleVoiceIds);
    private static readonly HashSet<string> FemaleHeadSet   = new(FemaleHeadIds);
    private static readonly HashSet<string> FemaleTopSet    = new(PredefinedSets.Select(s => s.TopId).Concat(UsecTopIds));
    private static readonly HashSet<string> FemaleBottomSet = new(PredefinedSets.Select(s => s.BottomId).Concat(UsecBottomIds));
    private static readonly HashSet<string> FemaleHandsSet  = new(PredefinedSets.Select(s => s.HandsId).Concat(UsecHandsIds));

    private static ISptLogger<FemalePmcAppearanceService> _logger = null!;
    private static RandomUtil _rng = null!;
    private static TemplateTable _templates = null!;
    private static readonly object DetectLock = new();
    // Bots can be generated in parallel; volatile so a set flag means the results are visible.
    private static volatile bool _detected;
    private static bool _wacAvailable;
    private static bool _dlcAvailable;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _logger = logger;
        _rng = randomUtil;
        _templates = templateTable;

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
        new Harmony("com.20fpsguy.femalepmcvoice.appearance").Patch(target, postfix: postfix);

        logger.Success("[FemalePmcAppearance] USEC female appearance completion patch applied.");
        return Task.CompletedTask;
    }

    // Neither mod adds its parts to the bot pools, only to the customization table, and they
    // may register after us, so look once when the first bot is generated.
    private static void DetectMods()
    {
        lock (DetectLock)
        {
            if (_detected) return;

            var ids = _templates.Customization.Keys.Select(k => k.ToString()).ToHashSet();
            _wacAvailable = FemaleHeadIds.Any(ids.Contains);
            _dlcAvailable = UsecTopIds.Any(ids.Contains);

            if (_wacAvailable)
                _logger.Info($"[FemalePmcAppearance] W.A.A.C. found, DLC.FemaleClothes {(_dlcAvailable ? "found" : "not found")}.");
            else
                _logger.Warning("[FemalePmcAppearance] W.A.A.C. not found (it provides the female heads). Appearance completion disabled.");

            _detected = true;
        }
    }

    private static void PrepareAndGenerateBotPostfix(ref BotBase __result, BotGenerationDetails botGenerationDetails)
    {
        try
        {
            if (!_detected) DetectMods();
            if (!_wacAvailable) return;
            if (__result is null) return;
            if (!botGenerationDetails.IsPmc || botGenerationDetails.Side != "Usec") return;

            var cust = __result.Customization;
            if (cust is null) return;

            var headFemale  = IsIn(FemaleHeadSet, cust.Head);
            var voiceFemale = IsIn(FemaleVoiceSet, cust.Voice);
            var bodyFemale  = IsIn(FemaleTopSet, cust.Body);
            var feetFemale  = IsIn(FemaleBottomSet, cust.Feet);
            var handsFemale = IsIn(FemaleHandsSet, cust.Hands);

            var anyFemale = headFemale || voiceFemale || bodyFemale || feetFemale || handsFemale;
            var allFemale = headFemale && voiceFemale && bodyFemale && feetFemale && handsFemale;

            if (!anyFemale || allFemale) return;

            if (!headFemale)
                cust.Head = _rng.GetArrayValue(FemaleHeadIds);

            if (!voiceFemale)
                cust.Voice = _rng.GetArrayValue(AllFemaleVoiceIds);

            if (_dlcAvailable && !_rng.GetChance100(PredefinedSetChancePercent))
                ApplyRandomUsecMix(cust);
            else
                ApplyPredefinedSet(cust);

            _logger.Debug("[FemalePmcAppearance] Completed partial female appearance on a USEC PMC.");
        }
        catch (Exception e)
        {
            _logger.Error($"[FemalePmcAppearance] Postfix error: {e}");
        }
    }

    private static bool IsIn(HashSet<string> set, MongoId? id) => id is { } v && set.Contains(v);

    private static void ApplyPredefinedSet(BotCustomization cust)
    {
        var set = _rng.GetArrayValue(PredefinedSets);
        cust.Body  = set.TopId;
        cust.Feet  = set.BottomId;
        cust.Hands = set.HandsId;
    }

    private static void ApplyRandomUsecMix(BotCustomization cust)
    {
        cust.Body  = _rng.GetArrayValue(UsecTopIds);
        cust.Feet  = _rng.GetArrayValue(UsecBottomIds);
        cust.Hands = _rng.GetArrayValue(UsecHandsIds);
    }

    public record ClothingSet(string TopId, string BottomId, string HandsId);
}
