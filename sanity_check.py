import sqlite3
import os

db_path = "tree_kingdoms.db"

# Validation map based on CustomOfficers.tres
# SourceID: (MaxCols, MaxRows_Partial)
# This is a bit complex to map perfectly, but we can do a sanity check.
# Source 7: Max X=3 (if Y=0), Max X=1 (if Y=1)
# Source 0: 4x4 (mostly)
# Source 1: 4x3
# Source 2: 3x3
# Source 3: 4x3

def is_valid(src, coords):
    try:
        x, y = map(int, coords.split(','))
    except:
        return False
        
    if src == 7:
        if y == 0: return 0 <= x <= 3
        if y == 1: return 0 <= x <= 1
        return False
    # Add other sources if needed, but 7 was the main issue.
    return True

conn = sqlite3.connect(db_path)
cursor = conn.cursor()
cursor.execute("SELECT officer_id, name, portrait_source_id, portrait_coords FROM officers")
rows = cursor.fetchall()

invalid_found = []
for r in rows:
    if not is_valid(r[2], r[3]):
        invalid_found.append(r)

if invalid_found:
    print(f"Found {len(invalid_found)} invalid records:")
    for inv in invalid_found:
        print(inv)
else:
    print("No other invalid Source 7 coordinates found.")

conn.close()
