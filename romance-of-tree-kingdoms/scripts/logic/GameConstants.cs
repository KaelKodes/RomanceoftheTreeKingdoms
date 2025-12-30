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
    public const string RANK_REGULAR = "Regular";
    public const string RANK_OFFICER = "Officer";
    public const string RANK_CAPTAIN = "Captain";
    public const string RANK_GENERAL = "General";
    public const string RANK_SOVEREIGN = "Sovereign";

    // Troop Limits
    public const int TROOPS_VOLUNTEER = 100;
    public const int TROOPS_REGULAR = 250;
    public const int TROOPS_OFFICER = 500;
    public const int TROOPS_CAPTAIN = 1000;
    public const int TROOPS_GENERAL = 3000;
    public const int TROOPS_SOVEREIGN = 5000;

    public static string GetRankTitle(int level)
    {
        return level switch
        {
            1 => RANK_REGULAR,
            2 => RANK_OFFICER,
            3 => RANK_CAPTAIN,
            4 => RANK_GENERAL,
            5 => RANK_SOVEREIGN,
            _ => RANK_VOLUNTEER // 0 or default
        };
    }

    public static int GetMaxTroops(string rank)
    {
        return rank switch
        {
            RANK_VOLUNTEER => TROOPS_VOLUNTEER,
            RANK_REGULAR => TROOPS_REGULAR,
            RANK_OFFICER => TROOPS_OFFICER,
            RANK_CAPTAIN => TROOPS_CAPTAIN,
            RANK_GENERAL => TROOPS_GENERAL,
            RANK_SOVEREIGN => TROOPS_SOVEREIGN,
            _ => TROOPS_VOLUNTEER
        };
    }

    public static int GetRankLevel(string rank)
    {
        return rank switch
        {
            RANK_VOLUNTEER => 0,
            RANK_REGULAR => 1,
            RANK_OFFICER => 2,
            RANK_CAPTAIN => 3,
            RANK_GENERAL => 4,
            RANK_SOVEREIGN => 5,
            _ => 0
        };
    }
}
