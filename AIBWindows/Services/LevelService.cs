using System;

namespace AIB.Services;

public static class LevelService
{
    // Limiares de XP (Mensagens)
    private static readonly int[] XPThresholds = { 0, 20, 50, 100, 200, 350, 600, 1000, 1500 };

    public static int GetLevel(int xp)
    {
        for (int i = XPThresholds.Length - 1; i >= 0; i--)
        {
            if (xp >= XPThresholds[i])
                return i + 1;
        }
        return 1;
    }

    public static int GetXPForNextLevel(int currentLevel)
    {
        if (currentLevel >= 9) return XPThresholds[8]; // Max level
        return XPThresholds[currentLevel]; // currentLevel (1-indexed) already points to the next threshold index
    }

    public static int GetXPForCurrentLevel(int currentLevel)
    {
        if (currentLevel < 1) return 0;
        return XPThresholds[currentLevel - 1];
    }

    public static int GetMaxTokensForLevel(int level)
    {
        return level switch
        {
            1 => 3072,
            2 => 5120,
            3 => 7168,
            4 => 12288,
            5 => 17408,
            6 => 22528,
            7 => 32768,
            8 => 43008,
            _ => 53248 // Level 9+
        };
    }
}
