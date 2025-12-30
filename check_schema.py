
import sqlite3
import os

db_path = r"d:\Kael Kodes\Tree Kingdoms\tree_kingdoms.db"
conn = sqlite3.connect(db_path)
cursor = conn.cursor()

print("--- officers schema ---")
cursor.execute("PRAGMA table_info(officers)")
for row in cursor.fetchall():
    print(row)

print("\n--- game_state schema ---")
cursor.execute("PRAGMA table_info(game_state)")
for row in cursor.fetchall():
    print(row)

conn.close()
