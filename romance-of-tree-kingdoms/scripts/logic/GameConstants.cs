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
    // Rank Names
    public const string RANK_VOLUNTEER = "Volunteer";
    public const string RANK_RECRUIT = "Recruit";
    public const string RANK_SOLDIER = "Soldier";
    public const string RANK_VETERAN = "Veteran";
    public const string RANK_SERGEANT = "Sergeant";
    public const string RANK_LIEUTENANT = "Lieutenant";
    public const string RANK_CAPTAIN = "Captain";
    public const string RANK_MAJOR = "Major";
    public const string RANK_GENERAL = "General";
    public const string RANK_COMMANDER = "Commander";
    public const string RANK_SOVEREIGN = "Sovereign";

    // Legacy Support (Mapping)
    public const string RANK_REGULAR = RANK_RECRUIT;
    public const string RANK_OFFICER = RANK_SERGEANT;

    // Troop Limits (Synchronized with GetMaxTroopsByLevel)
    public const int TROOPS_VOLUNTEER = 500;
    public const int TROOPS_REGULAR = 1000; // Matches level 1
    public const int TROOPS_OFFICER = 4500; // Matches level 4 (Sergeant)
    public const int TROOPS_CAPTAIN = 8000; // Matches level 6
    public const int TROOPS_GENERAL = 13000; // Matches level 8
    public const int TROOPS_SOVEREIGN = 20000; // Matches level 10

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
            case 0: return RANK_VOLUNTEER;
            case 1: return RANK_RECRUIT;
            case 2: return RANK_SOLDIER;
            case 3: return RANK_VETERAN;
            case 4: return RANK_SERGEANT;
            case 5: return RANK_LIEUTENANT;
            case 6: return RANK_CAPTAIN;
            case 7: return RANK_MAJOR;
            case 8: return RANK_GENERAL;
            case 9: return RANK_COMMANDER;
            case 10: return RANK_SOVEREIGN;
            default: return RANK_RECRUIT;
        }
    }

    // RENAMED to avoid any potential clashing with local methods or stale signatures
    public static int GetMaxTroopsByLevel(int level)
    {
        switch (level)
        {
            case 0: return 500;
            case 1: return 1000;
            case 2: return 2000;
            case 3: return 3000;
            case 4: return 4500;
            case 5: return 6000;
            case 6: return 8000;
            case 7: return 10000;
            case 8: return 13000;
            case 9: return 16000;
            case 10: return 20000;
            default: return 500;
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
            case RANK_VOLUNTEER: return 0;
            case RANK_RECRUIT: return 1;
            case "Regular": return 1; // Backward compatibility
            case RANK_SOLDIER: return 2;
            case RANK_VETERAN: return 3;
            case RANK_SERGEANT: return 4;
            case "Officer": return 4; // Backward compatibility
            case RANK_LIEUTENANT: return 5;
            case RANK_CAPTAIN: return 6;
            case RANK_MAJOR: return 7;
            case RANK_GENERAL: return 8;
            case RANK_COMMANDER: return 9;
            case RANK_SOVEREIGN: return 10;
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
