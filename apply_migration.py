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
    
    # Check for is_hq
    cursor.execute("PRAGMA table_info(cities)")
    columns = cursor.fetchall()
    col_names = [c[1] for c in columns]
    
    if "is_hq" not in col_names:
        print("Adding is_hq column...")
        cursor.execute("ALTER TABLE cities ADD COLUMN is_hq INTEGER DEFAULT 0")
    else:
        print("is_hq already exists.")

    if "decay_turns" not in col_names:
        print("Adding decay_turns column...")
        cursor.execute("ALTER TABLE cities ADD COLUMN decay_turns INTEGER DEFAULT 0")
    else:
        print("decay_turns already exists.")

    conn.commit()
    print("Migration applied successfully.")
    conn.close()
except Exception as e:
    print(f"ERROR: {e}")
