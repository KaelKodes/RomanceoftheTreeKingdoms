import sqlite3
conn = sqlite3.connect('tree_kingdoms.db')
cursor = conn.cursor()
cursor.execute("SELECT officer_id, name, portrait_source_id, portrait_coords FROM officers WHERE portrait_source_id = 7 OR name LIKE '%Zhou%Yu%'")
rows = cursor.fetchall()
for r in rows:
    print(r)
conn.close()
