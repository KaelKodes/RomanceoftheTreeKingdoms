import sqlite3
import os

db_path = "d:/Kael Kodes/Tree Kingdoms/tree_kingdoms.db"
if not os.path.exists(db_path):
    print(f"DB not found at {db_path}")
    exit(1)

conn = sqlite3.connect(db_path)
cursor = conn.cursor()

print("--- Player Officer Data ---")
cursor.execute("SELECT officer_id, name, is_player, strength, base_strength, leadership, base_leadership, intelligence, base_intelligence, politics, base_politics, charisma, base_charisma, stat_points FROM officers WHERE is_player = 1")
row = cursor.fetchone()
if row:
    cols = [description[0] for description in cursor.description]
    for col, val in zip(cols, row):
        print(f"{col}: {val}")
else:
    print("No player officer found.")

conn.close()
