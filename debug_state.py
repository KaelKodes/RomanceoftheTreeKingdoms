import sqlite3
import os

db_path = "tree_kingdoms.db"

if not os.path.exists(db_path):
    print(f"Database not found at {db_path}")
else:
    conn = sqlite3.connect(db_path)
    c = conn.cursor()
    try:
        c.execute("SELECT officer_id, name, rank, reputation, battles_won, is_player FROM officers WHERE is_player = 1")
        row = c.fetchone()
        if row:
            print(f"Player Found:")
            print(f"ID: {row[0]}")
            print(f"Name: {row[1]}")
            print(f"Rank: '{row[2]}' (Type: {type(row[2])})")
            print(f"Reputation: {row[3]}")
            print(f"Battles Won: {row[4]}")
        else:
            print("No player found with is_player=1")

        # Also check schema for rank column
        c.execute("PRAGMA table_info(officers)")
        cols = c.fetchall()
        for col in cols:
            if col[1] == 'rank':
                print(f"Column 'rank' info: {col}")
                
    except Exception as e:
        print(f"Error: {e}")
    finally:
        conn.close()
