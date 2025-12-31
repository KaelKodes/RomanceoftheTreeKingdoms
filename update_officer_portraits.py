import sqlite3
import random

db_path = "tree_kingdoms.db"

updates = [
    ("Zhong Hui", 0, "2,0"),
    ("Sun Quan", 7, "2,0"),
    ("Sun Ce", 7, "0,0"),
    ("Xu Shu", 3, "0,0"),
    ("Diaochan", 3, "2,0"),
    ("Chen Gong", 3, "3,0"),
    ("Yuan Shao", 3, "0,1"),
    ("Hu Ji", 3, "0,2"),
    ("Lin Zhi", 4, "3,1"),
    ("Zhao Yun", 5, "0,1"),
    ("Sun Jian", 7, "0,0"),
    ("Lu Meng", 7, "0,1"),
    ("Lu Zun", 7, "1,1"),
    ("Xiahou Dun", 8, "0,0")
]

conn = sqlite3.connect(db_path)
cursor = conn.cursor()

for name, src, coords in updates:
    # Check if exists
    cursor.execute("SELECT officer_id FROM officers WHERE name = ?", (name,))
    row = cursor.fetchone()
    if row:
        print(f"Updating {name} ({row[0]}) to Source {src}, Coords {coords}")
        cursor.execute("UPDATE officers SET portrait_source_id = ?, portrait_coords = ? WHERE officer_id = ?", (src, coords, row[0]))
    else:
        print(f"Adding {name} to Source {src}, Coords {coords}")
        # Add basic stats for new officers
        cursor.execute("""
            INSERT INTO officers (name, leadership, intelligence, strength, politics, charisma, rank, reputation, troops, max_troops, max_action_points, current_action_points, portrait_source_id, portrait_coords, is_player)
            VALUES (?, ?, ?, ?, ?, ?, 'Volunteer', 0, 500, 500, 3, 3, ?, ?, 0)
        """, (name, random.randint(50, 80), random.randint(50, 80), random.randint(50, 80), random.randint(50, 80), random.randint(50, 80), src, coords))

conn.commit()
conn.close()
print("Database updated successfully.")
