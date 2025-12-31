using System.Collections.Generic;

public enum FormationShape
{
    Surrounded, // Default 3x3
    Vanguard,   // Lead the charge (Officer at Front)
    RearGuard,  // Command from behind (Officer at Back)
    Line,       // Wide line
}

public static class FormationHelper
{
    // Returns a 5x5 boolean grid based on the shape.
    // Officer is always at 2,2 (Center).
    // TRUE means a slot has troops.
    public static bool[,] GetSlots(FormationShape shape)
    {
        bool[,] slots = new bool[5, 5];

        switch (shape)
        {
            case FormationShape.Surrounded:
                Fill(slots, true);
                break;

            case FormationShape.Vanguard:
                // Lead means Officer is in Front. Troops behind.
                // Rows 2, 3, 4 active.
                SetRow(slots, 2, true);
                SetRow(slots, 3, true);
                SetRow(slots, 4, true);
                break;

            case FormationShape.RearGuard:
                // Officer is in Back. Troops in front.
                // Rows 0, 1, 2 active.
                SetRow(slots, 0, true);
                SetRow(slots, 1, true);
                SetRow(slots, 2, true);
                break;

            case FormationShape.Line:
                // Wide Front
                SetRow(slots, 0, true);
                SetRow(slots, 1, true);
                break;
        }

        // Always ensure center is valid
        slots[2, 2] = true;

        return slots;
    }

    private static void Fill(bool[,] slots, bool val)
    {
        for (int x = 0; x < 5; x++)
            for (int y = 0; y < 5; y++)
                slots[x, y] = val;
    }

    private static void SetRow(bool[,] slots, int y, bool val)
    {
        for (int x = 0; x < 5; x++) slots[x, y] = val;
    }
}
