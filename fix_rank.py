import sqlite3
import os

db_path = "tree_kingdoms.db"

if not os.path.exists(db_path):
    print(f"Database not found at {db_path}")
else:
    conn = sqlite3.connect(db_path)
    c = conn.cursor()
    try:
        # Check current
        c.execute("SELECT rank FROM officers WHERE is_player=1")
        print(f"Current Rank: {c.fetchone()[0]}")

        # Update
        c.execute("UPDATE officers SET rank = 'Volunteer', max_troops = 1000 WHERE is_player = 1 AND rank = 'Officer' AND reputation < 100")
        print(f"Rows Updated: {c.rowcount}")
        
        conn.commit()
    except Exception as e:
        print(f"Error: {e}")
    finally:
        conn.close()
