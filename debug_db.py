import sqlite3
import os

db_path = 'd:/Kael Kodes/Tree Kingdoms/tree_kingdoms.db'

if not os.path.exists(db_path):
    print(f"Database not found at {db_path}")
    exit()

conn = sqlite3.connect(db_path)
c = conn.cursor()

print("--- FACTIONS ---")
try:
    c.execute("SELECT * FROM factions")
    for row in c.fetchall():
        print(row)
except Exception as e:
    print(e)

print("\n--- OFFICERS (First 10) ---")
try:
    c.execute("SELECT officer_id, name, faction_id, is_player FROM officers LIMIT 10")
    for row in c.fetchall():
        print(row)
except Exception as e:
    print(e)

print("\n--- TURN MANAGER QUERY TEST ---")
try:
    c.execute("""
        SELECT f.faction_id, MAX(o.strategy) as strat, MAX(o.leadership) as lead
        FROM factions f
        JOIN officers o ON f.faction_id = o.faction_id
        GROUP BY f.faction_id
    """)
    for row in c.fetchall():
        print(row)
except Exception as e:
    print(e)

conn.close()
