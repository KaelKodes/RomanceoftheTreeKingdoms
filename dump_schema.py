import sqlite3
import os

db_path = r"D:\Kael Kodes\Tree Kingdoms\tree_kingdoms.db"

if not os.path.exists(db_path):
    print(f"ERROR: Database not found at {db_path}")
    exit(1)

print(f"Connecting to {db_path}...")
try:
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    print("\n--- TABLE: cities ---")
    cursor.execute("PRAGMA table_info(cities)")
    columns = cursor.fetchall()
    for col in columns:
        print(col)
        
    print("\n--- TABLE: officers ---")
    cursor.execute("PRAGMA table_info(officers)")
    columns = cursor.fetchall()
    for col in columns:
        print(col)

    conn.close()
except Exception as e:
    print(f"ERROR: {e}")
