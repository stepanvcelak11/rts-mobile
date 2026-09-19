using System.Collections.Generic;

// Plain data objects mirroring docs/02-DATA-SCHEMA.md. Numbers stay `decimal` here so that the
// simulation can convert them to Fix64 exactly; nothing in this assembly references UnityEngine.
namespace RTS.Data
{
    /// <summary>Resource costs / amounts keyed by resource id ("food", "wood", "gold").</summary>
    public sealed class Cost : Dictionary<string, decimal> { }

    public sealed class ArmorDef
    {
        public decimal melee;
        public decimal ranged;
        public decimal siege;
    }

    public sealed class StatsDef
    {
        public decimal hp;
        public decimal speed;
        public decimal los;
        public ArmorDef armor = new ArmorDef();
        public string sizeClass = "small";
    }

    public sealed class AttackDef
    {
        public string id;
        public decimal damage;
        public string type = "melee";
        public decimal range = 1;
        public decimal minRange;
        public decimal cooldownSeconds = 1.5m;
        public string projectile;
        public Dictionary<string, decimal> multipliers = new Dictionary<string, decimal>();
    }

    public sealed class BehaviourDef
    {
        public bool canGather;
        public bool canBuild;
        public string aggro = "defensive";
        public decimal leashRange = 8;
        public decimal fleeHpPercent;
    }

    public sealed class GatherDef
    {
        public List<string> allowed = new List<string>();
        public decimal rateMultiplier = 1;
    }

    public sealed class ViewDef
    {
        public string prefab;
        public string icon;
        public decimal scale = 1;
        public string voiceSet;
        public string constructionPrefab;
    }

    public sealed class UnitDef
    {
        public string id;
        public string name;
        public string civ = "common";
        public string age = "age.1";
        public List<string> trainedAt = new List<string>();
        public Cost cost = new Cost();
        public decimal trainSeconds;
        public int population = 1;
        public int batchSize = 1;
        public StatsDef stats = new StatsDef();
        public List<AttackDef> attacks = new List<AttackDef>();
        public List<string> tags = new List<string>();
        public BehaviourDef behaviour = new BehaviourDef();
        public GatherDef gather;
        public ViewDef view = new ViewDef();
    }

    public sealed class PlacementDef
    {
        public List<string> terrain = new List<string> { "grass", "dirt" };
        public decimal minDistanceToEnemyBuilding;
        public RequiresNearDef requiresNear;
    }

    public sealed class RequiresNearDef
    {
        public string id;
        public decimal range;
    }

    public sealed class BuildingAttackDef
    {
        public decimal damage;
        public string type = "ranged";
        public decimal range;
        public decimal cooldownSeconds;
        public decimal garrisonBonus;
    }

    public sealed class QueueDef
    {
        public int slots;
        public int parallel;
    }

    public sealed class BuildingDef
    {
        public string id;
        public string name;
        public string civ = "common";
        public string age = "age.1";
        public Cost cost = new Cost();
        public decimal buildSeconds;
        public List<int> footprint = new List<int> { 1, 1 };
        public StatsDef stats = new StatsDef();
        public PlacementDef placement = new PlacementDef();
        public List<string> trains = new List<string>();
        public List<string> researches = new List<string>();
        public List<string> dropOff = new List<string>();
        public int populationProvided;
        public BuildingAttackDef attack;
        public int garrison;
        public int limit;
        public QueueDef queue = new QueueDef();
        public ViewDef view = new ViewDef();
        /// <summary>Resource node id this building doubles as once complete (mill = farm plots).</summary>
        public string gatherNode;
        /// <summary>True for the market: enables buy/sell trades for its owner.</summary>
        public bool market;

        public int FootprintW => footprint.Count > 0 ? footprint[0] : 1;
        public int FootprintH => footprint.Count > 1 ? footprint[1] : 1;
    }

    public sealed class ModifierDef
    {
        public string target;
        public string stat;
        public string op = "mul";
        public decimal value;
    }

    public sealed class SpawnDef
    {
        public string id;
        public int count = 1;
    }

    public sealed class HomeCityDef
    {
        public decimal xpPerShipmentBase = 300;
        public decimal xpGrowth = 1.25m;
        public List<string> deck = new List<string>();
        public int deckSize = 8;
    }

    public sealed class CivAiDef
    {
        public string buildOrder;
        public List<string> preferredComp = new List<string>();
    }

    public sealed class CivDef
    {
        public string id;
        public string name;
        public string tagline;
        public string color = "#FFFFFF";
        public List<SpawnDef> startingUnits = new List<SpawnDef>();
        public Cost startingStockpile = new Cost();
        public List<string> uniqueUnits = new List<string>();
        public List<string> uniqueBuildings = new List<string>();
        public Dictionary<string, string> replacements = new Dictionary<string, string>();
        public List<ModifierDef> modifiers = new List<ModifierDef>();
        public HomeCityDef homeCity = new HomeCityDef();
        public CivAiDef ai = new CivAiDef();
    }

    public sealed class TechDef
    {
        public string id;
        public string name;
        public string kind = "tech";          // "tech" | "shipment"
        public string age = "age.1";
        public List<string> researchedAt = new List<string>();
        public Cost cost = new Cost();
        public decimal researchSeconds;
        public List<ModifierDef> effects = new List<ModifierDef>();
        public List<SpawnDef> spawns = new List<SpawnDef>();
        public List<string> prerequisites = new List<string>();
        public bool oncePerGame;
    }

    /// <summary>One of the bonuses a player picks when advancing (AoE3 politicians).</summary>
    public sealed class AgeChoiceDef
    {
        public string id;
        public string name;
        public string description;
        public List<ModifierDef> effects = new List<ModifierDef>();
        public List<SpawnDef> spawns = new List<SpawnDef>();
        public Cost stockpile = new Cost();
    }

    public sealed class AgeDef
    {
        public string id;
        public string name;
        public Cost cost = new Cost();
        public decimal researchSeconds;
        public string at;
        public List<AgeChoiceDef> choices = new List<AgeChoiceDef>();
    }

    public sealed class ResourceNodeDef
    {
        public string id;
        public decimal amount;
        public List<int> footprint = new List<int> { 1, 1 };
        public bool depletes = true;
        public decimal regrowSeconds;
        public bool mobile;
        public decimal fleeRange;
        public string requiresBuilding;
        /// <summary>Picked up instantly by the first villager to reach it: grants `amount` of its resource and XP.</summary>
        public bool treasure;

        public int FootprintW => footprint.Count > 0 ? footprint[0] : 1;
        public int FootprintH => footprint.Count > 1 ? footprint[1] : 1;
    }

    public sealed class MarketDef
    {
        public bool enabled;
        public decimal basePrice = 100;
        public decimal priceStep = 3;
        public decimal decayPerMinute = 4;
    }

    public sealed class EconomyDef
    {
        public List<string> resources = new List<string>();
        public Cost startingStockpile = new Cost();
        public decimal stockpileCap = 99999;
        public Dictionary<string, Cost> gatherRates = new Dictionary<string, Cost>();
        public decimal carryCapacity = 10;
        public decimal depositRadius = 1.5m;
        public List<ResourceNodeDef> resourceNodes = new List<ResourceNodeDef>();
        public MarketDef market = new MarketDef();
        public int populationCapBase = 10;
        public int populationCapMax = 200;
    }

    /// <summary>A rectangle of terrain painted over the default (grass) map.</summary>
    public sealed class TerrainPatchDef
    {
        public int x, y, w, h;
        public string terrain = "grass";
        public int elevation;
    }

    public sealed class NodePlacementDef
    {
        public string id;
        public int x, y;
        public decimal amount = -1;   // -1 = default from the node definition
    }

    public sealed class StartPositionDef
    {
        public int x, y;
    }

    /// <summary>A wild unit placed on the map (treasure guardians, wildlife).</summary>
    public sealed class GuardianDef
    {
        public string id;
        public int x, y;
    }

    public sealed class MapDef
    {
        public string id = "map.default";
        public string name = "Skirmish";
        public List<int> size = new List<int> { 64, 64 };
        public int players = 2;
        public List<TerrainPatchDef> patches = new List<TerrainPatchDef>();
        public List<NodePlacementDef> nodes = new List<NodePlacementDef>();
        public List<StartPositionDef> starts = new List<StartPositionDef>();
        public List<GuardianDef> guardians = new List<GuardianDef>();

        public int Width => size.Count > 0 ? size[0] : 64;
        public int Height => size.Count > 1 ? size[1] : 64;
    }

    /// <summary>Wrapper for files of the form { "items": [ ... ] }.</summary>
    public sealed class ItemsFile<T>
    {
        public List<T> items = new List<T>();
    }
}
