using System.Collections.Generic;
using Godot;

public class TroopData
{
    public TroopType Type;
    public int Tier;
    public string Variant;
    public float GoldCostPerSoldier;
    public float FoodConsumptionRatio; // Per troop per day (base 1.0)
    public int TechRequirement;
    public int AttackBonus;
    public int DefenseBonus;
    public bool RequiresSiegeWorkshop;
}

public static class TroopDataManager
{
    private static readonly Dictionary<(TroopType, int), TroopData> _data = new Dictionary<(TroopType, int), TroopData>();

    static TroopDataManager()
    {
        // Tier 1 - Standard
        Add(TroopType.Infantry, 1, "Light Infantry", 0.0f, 1.0f, 0, 0, 0);
        Add(TroopType.Archer, 1, "Light Archers", 0.1f, 1.2f, 0, 0, 0);
        Add(TroopType.Cavalry, 1, "Light Cavalry", 0.2f, 2.0f, 0, 2, -1);

        // Tier 2 - Heavy
        Add(TroopType.Infantry, 2, "Heavy Infantry", 0.15f, 1.3f, 300, 2, 5);
        Add(TroopType.Archer, 2, "Heavy Archers", 0.25f, 1.5f, 400, 4, 2);
        Add(TroopType.Cavalry, 2, "Heavy Cavalry", 0.40f, 3.0f, 500, 8, 3);

        // Tier 3 - Elite
        Add(TroopType.Elite, 1, "Guard Infantry", 0.50f, 2.0f, 600, 10, 10);
        Add(TroopType.Elite, 2, "Imperial Guard", 0.80f, 2.5f, 800, 15, 15);

        // Siege
        Add(TroopType.Siege, 1, "Ram", 200.0f, 5.0f, 400, 20, 0, true);
        Add(TroopType.Siege, 2, "Catapult", 400.0f, 8.0f, 600, 40, 0, true);
    }

    private static void Add(TroopType type, int tier, string variant, float cost, float food, int tech, int atk, int def, bool siege = false)
    {
        _data[(type, tier)] = new TroopData
        {
            Type = type,
            Tier = tier,
            Variant = variant,
            GoldCostPerSoldier = cost,
            FoodConsumptionRatio = food,
            TechRequirement = tech,
            AttackBonus = atk,
            DefenseBonus = def,
            RequiresSiegeWorkshop = siege
        };
    }

    public static TroopData GetTroopData(TroopType type, int tier)
    {
        if (_data.TryGetValue((type, tier), out var data)) return data;
        // Fallback to Tier 1
        if (tier > 1 && _data.TryGetValue((type, 1), out var fallback)) return fallback;
        return null;
    }

    public static float CalculateOutfittingCost(TroopType type, int tier, int count)
    {
        var data = GetTroopData(type, tier);
        if (data == null) return 0;

        // Flat costs for siege
        if (data.Type == TroopType.Siege) return data.GoldCostPerSoldier;

        return data.GoldCostPerSoldier * count;
    }

    public static float GetDailyConsumption(int troopCount, TroopType type, int tier)
    {
        var data = GetTroopData(type, tier);
        float baseRate = 0.02f; // Base 2 units of food per 100 soldiers per day
        if (data == null) return troopCount * baseRate;

        return troopCount * baseRate * data.FoodConsumptionRatio;
    }
}
