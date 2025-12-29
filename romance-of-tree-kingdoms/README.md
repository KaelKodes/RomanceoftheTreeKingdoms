# Romance of Tree Kingdoms 🌳⚔️

**Romance of Tree Kingdoms** is a grand strategy role-playing game that blends turn-based empire management with real-time tactical battles. Inspired by classics like *Romance of the Three Kingdoms* and *Mount & Blade*, it puts you in the shoes of an officer navigating a chaotic world of warring factions.

## 🌟 Key Features

### 🗺️ Grand Strategy Layer
*   **Dynamic World Generation**: Every game starts with a procedurally generated political map, including 3 distinct factions, unique officers, and randomly assigned headquarters.
*   **Turn-Based Actions**: Manage your **Action Points (AP)** to travel, recruit officers, assist cities, or declare war.
*   **Supply Lines & Connectivity**: Conquest is strategic. You can only attack cities connected to your faction's territory. Getting cut off from your HQ cripples your offensive capabilities.
*   **Living World**:
    *   **Faction AI**: Rulers have distinct personalities (Aggressive vs. Cautious) that drive their expansion and diplomacy.
    *   **Ronin System**: Independent officers wander the map, looking for employment or opportunities.
    *   **Decay Mechanics**: Ungarrisoned cities lose order and can revert to neutrality if neglected.

### ⚔️ Tactical Battle Layer
*   **Real-Time Combat**: When diplomacy fails, take direct command on the battlefield.
*   **Officer-Led Units**: The strength of a unit depends on its commanding officer's **Combat** (Damage) and **Leadership** (Troop Capacity) stats.
*   **Tactical Depth**: 
    *   **Unit Types**: Command Infantry, Cavalry, and Archers with distinct roles.
    *   **Terrain**: Battle maps feature chokepoints, open plains, and defensive fortifications.
    *   **Capture Points**: Seize strategic nodes to gain the upper hand or decapitate the enemy army by defeating their commander.

### 📜 RPG Systems
*   **Officer Stats**: Characters are defined by 4 core attributes:
    *   **Leadership**: Determines troop count and recruitment chance.
    *   **Strategy**: Influences AI caution and tactical bonuses.
    *   **Combat**: Raw fighting power in duels and battles.
    *   **Politics**: Governing efficiency and economic growth.
*   **Relationships**: A dynamic web of friendships and rivalries affects recruitment success and battlefield synergy.

## 🛠️ Technical Stack

Built with **Godot 4.5.1 (C# / .NET)**.

*   **Data Persistence**: Powered by **SQLite**. The entire game state (world map, officer stats, turn history) is stored in a relational database (`tree_kingdoms.db`), allowing for complex queries and robust save/load functionality.
*   **Architecture**:
    *   `ActionManager`: Handles valid player and AI verbs (Travel, Attack, Recruit).
    *   `TurnManager`: Orchestrates the daily loop, AI processing, and "End of Day" conflict resolution.
    *   `WorldGenerator`: Handles the procedural setup of the initial game state.
    *   `BattleManager`: Bridges the strategic layer to the tactical scene context.

## 🚀 Getting Started

1.  **New Game**: Generates a fresh seed with random factions.
2.  **Character Creation**: Create your custom officer or play as an existing one.
3.  **The Loop**: 
    *   Use your turn to build strength or move to the front lines.
    *   End your turn to watch the AI factions make their moves.
    *   Resolve any declared battles (Tactical Mode) at the end of the day.

---
*Generated for Kael Kodes*
