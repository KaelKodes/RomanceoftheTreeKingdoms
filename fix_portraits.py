import sqlite3
import os

db_path = "tree_kingdoms.db"

if os.path.exists(db_path):
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    # Check current state
    cursor.execute("SELECT officer_id, name, portrait_source_id, portrait_coords FROM officers WHERE name = 'Zhou Yu'")
    row = cursor.fetchone()
    
    if row:
        print(f"Found Zhou Yu: Coords={row[3]}, Source={row[2]}")
        if row[3] == '3,1':
            cursor.execute("UPDATE officers SET portrait_coords = '3,0' WHERE officer_id = ?", (row[0],))
            conn.commit()
            print("Fixed Zhou Yu portrait coords to 3,0")
        else:
            print("Zhou Yu already has 3,0 or something else.")
    else:
        print("Zhou Yu not found in database.")
    
    # Also check for any other source 7, 3,1 invalid coords
    cursor.execute("SELECT officer_id, name FROM officers WHERE portrait_source_id = 7 AND portrait_coords = '3,1'")
    rows = cursor.fetchall()
    for r in rows:
        print(f"Found officer with invalid Source 7 coords (3,1): {r[1]}")
        # Fix to 1,1 or something valid
        cursor.execute("UPDATE officers SET portrait_coords = '1,1' WHERE officer_id = ?", (r[0],))
        print(f"Fixed {r[1]} to 1,1")
    
    conn.commit()
    conn.close()
else:
    print("Database not found.")
