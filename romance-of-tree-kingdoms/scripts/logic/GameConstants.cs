using Godot;
using System.Collections.Generic;

public enum UnitRole
{
    FRONTLINE,
    RANGED,
    SUPPORT,
    CAVALRY,
    LOGISTICS
}

public static class GameConstants
{
    // Rank Names (Legacy Support)
    public const string RANK_VOLUNTEER = "Volunteer";
    public const string RANK_REGULAR = "Regular";
    public const string RANK_OFFICER = "Officer";
    public const string RANK_CAPTAIN = "Captain";
    public const string RANK_GENERAL = "General";
    public const string RANK_SOVEREIGN = "Sovereign";

    // Troop Limits
    public const int TROOPS_VOLUNTEER = 100;
    public const int TROOPS_REGULAR = 400;
    public const int TROOPS_OFFICER = 1000;
    public const int TROOPS_CAPTAIN = 1500;
    public const int TROOPS_GENERAL = 3000;
    public const int TROOPS_SOVEREIGN = 5000;

    // Command Point (CP) Limits
    public const int CP_RULER = 5;
    public const int CP_GOVERNOR = 3;
    public const int CP_VASSAL = 0;

    // Action Point (AP) Limits
    public const int AP_BASELINE = 3;

    public static string GetRankTitle(int level)
    {
        switch (level)
        {
            case 0: return "Volunteer";
            case 1: return "Regular (9th)";
            case 2: return "Regular (8th)";
            case 3: return "Regular (7th)";
            case 4: return "Officer (6th)";
            case 5: return "Officer (5th)";
            case 6: return "Captain (4th)";
            case 7: return "Captain (3rd)";
            case 8: return "General (2nd)";
            case 9: return "Commander (1st)";
            case 10: return "Sovereign";
            default: return "Recruit";
        }
    }

    // RENAMED to avoid any potential clashing with local methods or stale signatures
    public static int GetMaxTroopsByLevel(int level)
    {
        switch (level)
        {
            case 0: return 100;
            case 1: return 200;
            case 2: return 400;
            case 3: return 600;
            case 4: return 800;
            case 5: return 1000;
            case 6: return 1500;
            case 7: return 2000;
            case 8: return 3000;
            case 9: return 4000;
            case 10: return 5000;
            default: return 100;
        }
    }

    // RENAMED to avoid any potential clashing with local methods or stale signatures
    public static int GetLevelByRankName(string rankName)
    {
        if (string.IsNullOrEmpty(rankName)) return 0;

        // Strip markers if present (e.g., "Recruit (9th)" -> "Recruit")
        string cleanName = rankName.Split(' ')[0];

        switch (cleanName)
        {
            case "Volunteer": return 0;
            case "Recruit": return 1;
            case "Soldier": return 2;
            case "Veteran": return 3;
            case "Sergeant": return 4;
            case "Lieutenant": return 5;
            case "Captain": return 6;
            case "Major": return 7;
            case "General": return 8;
            case "Commander": return 9;
            case "Sovereign": return 10;
            default: return 1;
        }
    }

    public static int GetRequiredRep(int targetLevel)
    {
        switch (targetLevel)
        {
            case 1: return 50;
            case 2: return 150;
            case 3: return 300;
            case 4: return 500;
            case 5: return 800;
            case 6: return 1200;
            case 7: return 1800;
            case 8: return 2500;
            case 9: return 3500;
            case 10: return 5000;
            default: return 0;
        }
    }
}
